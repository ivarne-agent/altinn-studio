package apptoken

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"errors"
	"fmt"
	"math/big"
	"sync"
	"testing"
	"time"

	. "github.com/onsi/gomega"

	opclock "altinn.studio/operator/internal/clock"
)

var errSimulatedKeyVault = errors.New("simulated key vault failure")

// fakeKeyVaultKeys is an in-memory Key Vault keys API. Every version signs with the same local key.
type fakeKeyVaultKeys struct {
	key        *ecdsa.PrivateKey
	versions   map[string][]keyVersion
	signErr    error
	signed     []string
	listCalls  int
	mu         sync.Mutex
	shortSigns bool
}

func newFakeKeyVaultKeys(t *testing.T) *fakeKeyVaultKeys {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}
	return &fakeKeyVaultKeys{key: key, versions: make(map[string][]keyVersion)}
}

func (f *fakeKeyVaultKeys) ListVersions(_ context.Context, name string) ([]keyVersion, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.listCalls++
	return f.versions[name], nil
}

func (f *fakeKeyVaultKeys) SignDigest(_ context.Context, name, version string, digest []byte) ([]byte, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.signErr != nil {
		return nil, f.signErr
	}
	f.signed = append(f.signed, name+"/"+version)
	if f.shortSigns {
		return []byte{1, 2, 3}, nil
	}
	r, s, err := ecdsa.Sign(rand.Reader, f.key, digest)
	if err != nil {
		return nil, fmt.Errorf("sign: %w", err)
	}
	signature := make([]byte, es256SignatureLength)
	r.FillBytes(signature[:32])
	s.FillBytes(signature[32:])
	return signature, nil
}

var selectionNow = time.Date(2026, 3, 23, 12, 0, 0, 0, time.UTC)

func TestSelectKeyVersion(t *testing.T) {
	const grace = time.Hour
	settled := keyVersion{Version: "settled", Enabled: true, Created: selectionNow.Add(-2 * time.Hour)}
	older := keyVersion{Version: "older", Enabled: true, Created: selectionNow.Add(-48 * time.Hour)}
	fresh := keyVersion{Version: "fresh", Enabled: true, Created: selectionNow.Add(-10 * time.Minute)}
	exactlyGrace := keyVersion{Version: "exact", Enabled: true, Created: selectionNow.Add(-grace)}

	tests := []struct {
		name     string
		want     string
		versions []keyVersion
	}{
		{name: "newest settled version", versions: []keyVersion{older, settled, fresh}, want: "settled"},
		{
			name:     "created exactly grace ago is settled",
			versions: []keyVersion{older, exactlyGrace, fresh},
			want:     "exact",
		},
		{name: "falls back to newest when none settled", versions: []keyVersion{fresh}, want: "fresh"},
		{
			name: "skips disabled",
			versions: []keyVersion{
				older,
				{Version: "disabled", Enabled: false, Created: settled.Created},
			},
			want: "older",
		},
		{
			name: "skips expired",
			versions: []keyVersion{
				older,
				{Version: "expired", Enabled: true, Created: settled.Created, Expires: selectionNow},
			},
			want: "older",
		},
		{
			name: "skips not yet valid",
			versions: []keyVersion{
				older,
				{Version: "future", Enabled: true, Created: settled.Created, NotBefore: selectionNow.Add(time.Minute)},
			},
			want: "older",
		},
		{
			name: "fallback skips disabled",
			versions: []keyVersion{
				fresh,
				{Version: "disabled", Enabled: false, Created: selectionNow.Add(-time.Minute)},
			},
			want: "fresh",
		},
		{name: "none usable", versions: []keyVersion{{Version: "disabled", Created: older.Created}}},
		{name: "no versions"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			g := NewWithT(t)
			got, ok := selectKeyVersion(tt.versions, selectionNow, grace)
			if tt.want == "" {
				g.Expect(ok).To(BeFalse())
				return
			}
			g.Expect(ok).To(BeTrue())
			g.Expect(got.Version).To(Equal(tt.want))
		})
	}
}

func newTestKeyVaultSigner(t *testing.T) (*KeyVaultSigner, *fakeKeyVaultKeys, *opclock.FakeClock) {
	t.Helper()
	keys := newFakeKeyVaultKeys(t)
	clock := opclock.NewFakeClockAt(selectionNow)
	signer := newKeyVaultSigner(keys, clock, testConfig)
	return signer, keys, clock
}

func TestKeyVaultSigner_MintsTokenWithSelectedVersion(t *testing.T) {
	g := NewWithT(t)

	signer, keys, clock := newTestKeyVaultSigner(t)
	keys.versions["app-token-signer-ttd"] = []keyVersion{
		{Version: "v1", Enabled: true, Created: selectionNow.Add(-72 * time.Hour)},
		{Version: "v2", Enabled: true, Created: selectionNow.Add(-2 * time.Hour)},
		{Version: "v3", Enabled: true, Created: selectionNow.Add(-time.Minute)},
	}

	token, err := Mint(context.Background(), signer, clock, "ttd", "app", testConfig())
	g.Expect(err).NotTo(HaveOccurred())

	verified := verifyToken(t, token.JWS, &keys.key.PublicKey)
	g.Expect(verified.Header.KeyID).To(Equal("v2"))
	g.Expect(keys.signed).To(Equal([]string{"app-token-signer-ttd/v2"}))
}

func TestKeyVaultSigner_SignsSHA256Digest(t *testing.T) {
	g := NewWithT(t)

	signer, keys, _ := newTestKeyVaultSigner(t)
	payload := []byte("header.payload")

	signature, err := signer.Sign(context.Background(), KeyRef{Name: "app-token-signer-ttd", Version: "v1"}, payload)
	g.Expect(err).NotTo(HaveOccurred())
	g.Expect(signature).To(HaveLen(es256SignatureLength))

	digest := sha256.Sum256(payload)
	r := new(big.Int).SetBytes(signature[:32])
	s := new(big.Int).SetBytes(signature[32:])
	g.Expect(ecdsa.Verify(&keys.key.PublicKey, digest[:], r, s)).To(BeTrue())
}

func TestKeyVaultSigner_CachesSelectedVersion(t *testing.T) {
	g := NewWithT(t)

	signer, keys, clock := newTestKeyVaultSigner(t)
	keys.versions["app-token-signer-ttd"] = []keyVersion{
		{Version: "v1", Enabled: true, Created: selectionNow.Add(-72 * time.Hour)},
	}

	for range 3 {
		key, err := signer.CurrentKey(context.Background(), "ttd")
		g.Expect(err).NotTo(HaveOccurred())
		g.Expect(key.Version).To(Equal("v1"))
	}
	g.Expect(keys.listCalls).To(Equal(1))

	keys.versions["app-token-signer-ttd"] = append(keys.versions["app-token-signer-ttd"],
		keyVersion{Version: "v2", Enabled: true, Created: selectionNow.Add(-2 * time.Hour)})
	clock.Advance(keyCacheTTL)

	key, err := signer.CurrentKey(context.Background(), "ttd")
	g.Expect(err).NotTo(HaveOccurred())
	g.Expect(key.Version).To(Equal("v2"))
	g.Expect(keys.listCalls).To(Equal(2))
}

func TestKeyVaultSigner_SignErrorDropsCachedVersion(t *testing.T) {
	g := NewWithT(t)

	signer, keys, _ := newTestKeyVaultSigner(t)
	keys.versions["app-token-signer-ttd"] = []keyVersion{
		{Version: "v1", Enabled: true, Created: selectionNow.Add(-72 * time.Hour)},
	}
	key, err := signer.CurrentKey(context.Background(), "ttd")
	g.Expect(err).NotTo(HaveOccurred())

	keys.signErr = errSimulatedKeyVault
	_, err = signer.Sign(context.Background(), key, []byte("payload"))
	g.Expect(err).To(MatchError(errSimulatedKeyVault))

	_, err = signer.CurrentKey(context.Background(), "ttd")
	g.Expect(err).NotTo(HaveOccurred())
	g.Expect(keys.listCalls).To(Equal(2))
}

func TestKeyVaultSigner_RejectsUnexpectedSignatureLength(t *testing.T) {
	g := NewWithT(t)

	signer, keys, _ := newTestKeyVaultSigner(t)
	keys.shortSigns = true

	_, err := signer.Sign(context.Background(), KeyRef{Name: "app-token-signer-ttd", Version: "v1"}, []byte("payload"))
	g.Expect(err).To(MatchError(errUnexpectedSignatureLen))
}

func TestKeyVaultSigner_FailsWithoutUsableVersion(t *testing.T) {
	g := NewWithT(t)

	signer, keys, _ := newTestKeyVaultSigner(t)
	keys.versions["app-token-signer-ttd"] = []keyVersion{
		{Version: "v1", Enabled: false, Created: selectionNow.Add(-72 * time.Hour)},
	}

	_, err := signer.CurrentKey(context.Background(), "ttd")
	g.Expect(err).To(MatchError(errNoUsableKeyVersion))
}
