// Package apptoken issues the short-lived JWTs that apps use to authenticate to Altinn Storage.
//
// The signing key is an EC P-256 key in Azure Key Vault, one per service owner. The private key never
// leaves the vault: the operator asks Key Vault to sign each token.
package apptoken

import (
	"context"
	"fmt"
	"time"

	"github.com/Azure/azure-sdk-for-go/sdk/azidentity"

	opclock "altinn.studio/operator/internal/clock"
	"altinn.studio/operator/internal/config"
	"altinn.studio/operator/internal/operatorcontext"
)

// KeyRef identifies one version of a signing key. Version is the token's kid.
type KeyRef struct {
	Created time.Time
	Name    string
	Version string
}

// Signer signs JWS input with an ES256 key.
type Signer interface {
	// CurrentKey returns the key version new tokens for the service owner should be signed with.
	CurrentKey(ctx context.Context, org string) (KeyRef, error)
	// Sign returns the ES256 signature of the JWS signing input as r||s (64 bytes), the JWS form.
	Sign(ctx context.Context, key KeyRef, payload []byte) ([]byte, error)
}

// NewSigner returns the signer for the environment: Key Vault in the cluster environments, and a
// FakeSigner in the local environment, which has no Key Vault.
func NewSigner(
	environment string,
	clock opclock.Clock,
	cfg func() config.AppTokenControllerConfig,
) (Signer, error) {
	if environment == operatorcontext.EnvironmentLocal {
		return NewFakeSigner(cfg().KeyNamePrefix)
	}

	credential, err := azidentity.NewDefaultAzureCredential(nil)
	if err != nil {
		return nil, fmt.Errorf("get Azure credentials: %w", err)
	}
	return NewKeyVaultSigner(config.KeyVaultURL(environment), credential, clock, cfg)
}
