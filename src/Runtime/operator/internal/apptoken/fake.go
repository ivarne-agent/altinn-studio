package apptoken

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"fmt"
	"sync"
	"time"
)

const fakeKeyVersion = "local"

// FakeSigner signs with an ECDSA P-256 key generated in memory. It is used in tests and in the local
// environment, which has no Key Vault.
type FakeSigner struct {
	key     *ecdsa.PrivateKey
	err     error
	version string
	prefix  string
	mu      sync.RWMutex
}

var _ Signer = (*FakeSigner)(nil)

// NewFakeSigner generates a new key. Key names are keyNamePrefix followed by the org.
func NewFakeSigner(keyNamePrefix string) (*FakeSigner, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, fmt.Errorf("generate fake signing key: %w", err)
	}
	return &FakeSigner{key: key, version: fakeKeyVersion, prefix: keyNamePrefix}, nil
}

// PublicKey returns the key that verifies the fake signer's tokens.
func (f *FakeSigner) PublicKey() *ecdsa.PublicKey {
	return &f.key.PublicKey
}

// SetVersion sets the key version CurrentKey returns, which becomes the token's kid.
func (f *FakeSigner) SetVersion(version string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.version = version
}

// SetError makes CurrentKey and Sign fail with err until it is cleared with nil.
func (f *FakeSigner) SetError(err error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.err = err
}

func (f *FakeSigner) CurrentKey(_ context.Context, org string) (KeyRef, error) {
	f.mu.RLock()
	defer f.mu.RUnlock()
	if f.err != nil {
		return KeyRef{}, f.err
	}
	return KeyRef{Name: f.prefix + org, Version: f.version, Created: time.Time{}}, nil
}

func (f *FakeSigner) Sign(_ context.Context, _ KeyRef, payload []byte) ([]byte, error) {
	f.mu.RLock()
	err := f.err
	f.mu.RUnlock()
	if err != nil {
		return nil, err
	}

	digest := sha256.Sum256(payload)
	r, s, err := ecdsa.Sign(rand.Reader, f.key, digest[:])
	if err != nil {
		return nil, fmt.Errorf("fake sign: %w", err)
	}
	signature := make([]byte, es256SignatureLength)
	r.FillBytes(signature[:es256SignatureLength/2])
	s.FillBytes(signature[es256SignatureLength/2:])
	return signature, nil
}
