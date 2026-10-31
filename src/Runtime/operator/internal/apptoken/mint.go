package apptoken

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/go-jose/go-jose/v4"
	"github.com/go-jose/go-jose/v4/jwt"
	"github.com/google/uuid"

	opclock "altinn.studio/operator/internal/clock"
	"altinn.studio/operator/internal/config"
)

const (
	ClaimOrg = "urn:altinn:org"
	ClaimApp = "urn:altinn:app"
)

// Token is a signed app token and the times the controller needs to schedule its refresh.
type Token struct {
	IssuedAt  time.Time
	ExpiresAt time.Time
	JWS       string
}

type appClaims struct {
	Org string `json:"urn:altinn:org"`
	App string `json:"urn:altinn:app"`
}

// Mint signs a token for the app org/app with the service owner's current key.
func Mint(
	ctx context.Context,
	signer Signer,
	clock opclock.Clock,
	org, app string,
	cfg config.AppTokenControllerConfig,
) (Token, error) {
	key, err := signer.CurrentKey(ctx, org)
	if err != nil {
		return Token{}, fmt.Errorf("get current signing key: %w", err)
	}

	joseSigner, err := jose.NewSigner(
		jose.SigningKey{Algorithm: jose.ES256, Key: &opaqueSigner{ctx: ctx, signer: signer, key: key}},
		(&jose.SignerOptions{}).WithType("JWT"),
	)
	if err != nil {
		return Token{}, fmt.Errorf("create JWS signer: %w", err)
	}

	// JWT times have second precision; truncate so the file's times match the claims.
	issuedAt := clock.Now().UTC().Truncate(time.Second)
	expiresAt := issuedAt.Add(cfg.TokenLifetime)
	org = strings.ToLower(org)

	jws, err := jwt.Signed(joseSigner).
		Claims(jwt.Claims{
			Issuer:    cfg.Issuer,
			Audience:  jwt.Audience{cfg.Audience},
			IssuedAt:  jwt.NewNumericDate(issuedAt),
			NotBefore: jwt.NewNumericDate(issuedAt),
			Expiry:    jwt.NewNumericDate(expiresAt),
			ID:        uuid.NewString(),
		}).
		Claims(appClaims{
			Org: org,
			App: org + "/" + strings.ToLower(app),
		}).
		Serialize()
	if err != nil {
		return Token{}, fmt.Errorf("sign app token with key %s version %s: %w", key.Name, key.Version, err)
	}

	return Token{JWS: jws, IssuedAt: issuedAt, ExpiresAt: expiresAt}, nil
}

// opaqueSigner adapts Signer to go-jose for a single Mint call. go-jose's OpaqueSigner has no
// context parameter, so the adapter carries the caller's.
//
//nolint:containedctx // Lives for one Mint call only; go-jose's OpaqueSigner cannot take a context.
type opaqueSigner struct {
	ctx    context.Context
	signer Signer
	key    KeyRef
}

// Public returns no key material: go-jose only reads the key ID, which becomes the kid header.
func (s *opaqueSigner) Public() *jose.JSONWebKey {
	return &jose.JSONWebKey{KeyID: s.key.Version, Algorithm: string(jose.ES256), Use: "sig"}
}

func (*opaqueSigner) Algs() []jose.SignatureAlgorithm {
	return []jose.SignatureAlgorithm{jose.ES256}
}

func (s *opaqueSigner) SignPayload(payload []byte, _ jose.SignatureAlgorithm) ([]byte, error) {
	signature, err := s.signer.Sign(s.ctx, s.key, payload)
	if err != nil {
		return nil, fmt.Errorf("sign app token: %w", err)
	}
	return signature, nil
}
