package apptoken

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/Azure/azure-sdk-for-go/sdk/azcore"
	"github.com/Azure/azure-sdk-for-go/sdk/security/keyvault/azkeys"

	opclock "altinn.studio/operator/internal/clock"
	"altinn.studio/operator/internal/config"
)

// keyCacheTTL bounds how long a chosen key version is reused before the versions are listed again.
// It is well below the key version grace, so a new version is picked up soon after it qualifies.
const keyCacheTTL = 5 * time.Minute

const es256SignatureLength = 64

var (
	errNoUsableKeyVersion     = errors.New("no enabled, unexpired key version")
	errUnexpectedSignatureLen = errors.New("unexpected ES256 signature length")
)

// keyVersion is the part of a Key Vault key version's properties that selection uses.
type keyVersion struct {
	Created   time.Time
	NotBefore time.Time
	Expires   time.Time
	Version   string
	Enabled   bool
}

// keyVaultKeys is the part of the Key Vault keys API the signer uses.
type keyVaultKeys interface {
	ListVersions(ctx context.Context, name string) ([]keyVersion, error)
	SignDigest(ctx context.Context, name, version string, digest []byte) ([]byte, error)
}

type cachedKey struct {
	fetchedAt time.Time
	key       KeyRef
}

// KeyVaultSigner signs with the key {prefix}{org} in Azure Key Vault.
type KeyVaultSigner struct {
	keys   keyVaultKeys
	clock  opclock.Clock
	config func() config.AppTokenControllerConfig
	cache  map[string]cachedKey
	mu     sync.Mutex
}

var _ Signer = (*KeyVaultSigner)(nil)

// NewKeyVaultSigner returns a signer for the vault at vaultURL. cfg is read on each key selection
// so that config reloads apply.
func NewKeyVaultSigner(
	vaultURL string,
	credential azcore.TokenCredential,
	clock opclock.Clock,
	cfg func() config.AppTokenControllerConfig,
) (*KeyVaultSigner, error) {
	client, err := azkeys.NewClient(vaultURL, credential, nil)
	if err != nil {
		return nil, fmt.Errorf("create Key Vault keys client: %w", err)
	}
	return newKeyVaultSigner(&azkeysClient{client: client}, clock, cfg), nil
}

func newKeyVaultSigner(
	keys keyVaultKeys,
	clock opclock.Clock,
	cfg func() config.AppTokenControllerConfig,
) *KeyVaultSigner {
	return &KeyVaultSigner{
		keys:   keys,
		clock:  clock,
		config: cfg,
		cache:  make(map[string]cachedKey),
	}
}

func (s *KeyVaultSigner) CurrentKey(ctx context.Context, org string) (KeyRef, error) {
	now := s.clock.Now()
	cfg := s.config()
	name := cfg.KeyNamePrefix + org

	s.mu.Lock()
	cached, ok := s.cache[org]
	s.mu.Unlock()
	if ok && cached.key.Name == name && now.Sub(cached.fetchedAt) < keyCacheTTL {
		return cached.key, nil
	}

	versions, err := s.keys.ListVersions(ctx, name)
	if err != nil {
		return KeyRef{}, fmt.Errorf("list versions of key %s: %w", name, err)
	}
	selected, ok := selectKeyVersion(versions, now, cfg.KeyVersionGrace)
	if !ok {
		return KeyRef{}, fmt.Errorf("%w: key %s", errNoUsableKeyVersion, name)
	}

	key := KeyRef{Name: name, Version: selected.Version, Created: selected.Created}
	s.mu.Lock()
	s.cache[org] = cachedKey{key: key, fetchedAt: now}
	s.mu.Unlock()
	return key, nil
}

func (s *KeyVaultSigner) Sign(ctx context.Context, key KeyRef, payload []byte) ([]byte, error) {
	digest := sha256.Sum256(payload)
	signature, err := s.keys.SignDigest(ctx, key.Name, key.Version, digest[:])
	if err != nil {
		// The version may have been disabled or removed; select again on the next attempt.
		s.forget(key)
		return nil, fmt.Errorf("sign with key %s version %s: %w", key.Name, key.Version, err)
	}
	if len(signature) != es256SignatureLength {
		return nil, fmt.Errorf("%w: %d", errUnexpectedSignatureLen, len(signature))
	}
	return signature, nil
}

func (s *KeyVaultSigner) forget(key KeyRef) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for org, cached := range s.cache {
		if cached.key == key {
			delete(s.cache, org)
		}
	}
}

// selectKeyVersion picks the newest usable version created at least grace ago, so that Storage has
// had time to fetch its public key. If no usable version is that old, as right after the key is
// created, it picks the newest usable version.
func selectKeyVersion(versions []keyVersion, now time.Time, grace time.Duration) (keyVersion, bool) {
	var newest, newestSettled keyVersion
	var found, foundSettled bool
	for _, v := range versions {
		if !v.Enabled || v.Version == "" ||
			(!v.Expires.IsZero() && !now.Before(v.Expires)) ||
			(!v.NotBefore.IsZero() && now.Before(v.NotBefore)) {
			continue
		}
		if !found || v.Created.After(newest.Created) {
			newest, found = v, true
		}
		if !v.Created.After(now.Add(-grace)) && (!foundSettled || v.Created.After(newestSettled.Created)) {
			newestSettled, foundSettled = v, true
		}
	}
	if foundSettled {
		return newestSettled, true
	}
	return newest, found
}

type azkeysClient struct {
	client *azkeys.Client
}

func (c *azkeysClient) ListVersions(ctx context.Context, name string) ([]keyVersion, error) {
	var versions []keyVersion
	pager := c.client.NewListKeyPropertiesVersionsPager(name, nil)
	for pager.More() {
		page, err := pager.NextPage(ctx)
		if err != nil {
			return nil, fmt.Errorf("list key versions page: %w", err)
		}
		for _, props := range page.Value {
			if props == nil || props.KID == nil || props.Attributes == nil {
				continue
			}
			versions = append(versions, keyVersion{
				Version:   props.KID.Version(),
				Enabled:   deref(props.Attributes.Enabled),
				Created:   deref(props.Attributes.Created),
				NotBefore: deref(props.Attributes.NotBefore),
				Expires:   deref(props.Attributes.Expires),
			})
		}
	}
	return versions, nil
}

func (c *azkeysClient) SignDigest(ctx context.Context, name, version string, digest []byte) ([]byte, error) {
	algorithm := azkeys.SignatureAlgorithmES256
	resp, err := c.client.Sign(ctx, name, version, azkeys.SignParameters{
		Algorithm: &algorithm,
		Value:     digest,
	}, nil)
	if err != nil {
		return nil, fmt.Errorf("key vault sign: %w", err)
	}
	return resp.Result, nil
}

func deref[T any](p *T) T {
	var zero T
	if p == nil {
		return zero
	}
	return *p
}
