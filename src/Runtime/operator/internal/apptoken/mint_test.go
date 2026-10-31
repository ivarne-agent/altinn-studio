package apptoken

import (
	"context"
	"crypto/ecdsa"
	"encoding/base64"
	"encoding/json"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/go-jose/go-jose/v4"
	"github.com/go-jose/go-jose/v4/jwt"
	. "github.com/onsi/gomega"

	opclock "altinn.studio/operator/internal/clock"
	"altinn.studio/operator/internal/config"
)

var errSimulatedSigner = errors.New("simulated signer failure")

func testConfig() config.AppTokenControllerConfig {
	return config.AppTokenControllerConfig{
		Audience:        "altinn-storage",
		Issuer:          "altinn-studio-operator",
		KeyNamePrefix:   "app-token-signer-",
		TokenLifetime:   2 * time.Hour,
		RefreshLeadTime: time.Hour,
		KeyVersionGrace: time.Hour,
	}
}

type verifiedToken struct {
	Header jose.Header
	Claims jwt.Claims
	App    appClaims
	RawAud json.RawMessage
}

// verifyToken checks the token's signature with go-jose and returns its header and claims.
func verifyToken(t *testing.T, token string, publicKey *ecdsa.PublicKey) verifiedToken {
	t.Helper()
	g := NewWithT(t)

	parsed, err := jwt.ParseSigned(token, []jose.SignatureAlgorithm{jose.ES256})
	g.Expect(err).NotTo(HaveOccurred())
	g.Expect(parsed.Headers).To(HaveLen(1))

	var result verifiedToken
	var raw map[string]json.RawMessage
	g.Expect(parsed.Claims(publicKey, &result.Claims, &result.App, &raw)).To(Succeed())
	result.Header = parsed.Headers[0]
	result.RawAud = raw["aud"]
	return result
}

func TestMint_ProducesVerifiableES256Token(t *testing.T) {
	g := NewWithT(t)

	signer, err := NewFakeSigner("app-token-signer-")
	g.Expect(err).NotTo(HaveOccurred())
	signer.SetVersion("0123456789abcdef")
	clock := opclock.NewFakeClockAt(time.Date(2026, 3, 23, 12, 0, 0, 500_000_000, time.UTC))

	token, err := Mint(context.Background(), signer, clock, "TTD", "My-App", testConfig())
	g.Expect(err).NotTo(HaveOccurred())

	wantIssuedAt := time.Date(2026, 3, 23, 12, 0, 0, 0, time.UTC)
	g.Expect(token.IssuedAt).To(Equal(wantIssuedAt))
	g.Expect(token.ExpiresAt).To(Equal(wantIssuedAt.Add(2 * time.Hour)))

	verified := verifyToken(t, token.JWS, signer.PublicKey())
	g.Expect(verified.Header.Algorithm).To(Equal(string(jose.ES256)))
	g.Expect(verified.Header.KeyID).To(Equal("0123456789abcdef"))
	g.Expect(verified.Header.ExtraHeaders).To(HaveKeyWithValue(jose.HeaderKey(jose.HeaderType), "JWT"))
	g.Expect(verified.Header.JSONWebKey).To(BeNil())

	g.Expect(verified.Claims.Issuer).To(Equal("altinn-studio-operator"))
	g.Expect(verified.Claims.Audience).To(Equal(jwt.Audience{"altinn-storage"}))
	g.Expect(string(verified.RawAud)).To(Equal(`"altinn-storage"`))
	g.Expect(verified.Claims.IssuedAt.Time().UTC()).To(Equal(wantIssuedAt))
	g.Expect(verified.Claims.NotBefore.Time().UTC()).To(Equal(wantIssuedAt))
	g.Expect(verified.Claims.Expiry.Time().UTC()).To(Equal(wantIssuedAt.Add(2 * time.Hour)))
	g.Expect(verified.Claims.ID).To(MatchRegexp(`^[0-9a-f-]{36}$`))
	g.Expect(verified.App).To(Equal(appClaims{Org: "ttd", App: "ttd/my-app"}))

	g.Expect(verified.Claims.ValidateWithLeeway(jwt.Expected{
		Issuer:      "altinn-studio-operator",
		AnyAudience: jwt.Audience{"altinn-storage"},
		Time:        clock.Now(),
	}, 0)).To(Succeed())
}

func TestMint_UsesUniqueTokenIDs(t *testing.T) {
	g := NewWithT(t)

	signer, err := NewFakeSigner("app-token-signer-")
	g.Expect(err).NotTo(HaveOccurred())
	clock := opclock.NewFakeClock()

	first, err := Mint(context.Background(), signer, clock, "ttd", "app", testConfig())
	g.Expect(err).NotTo(HaveOccurred())
	second, err := Mint(context.Background(), signer, clock, "ttd", "app", testConfig())
	g.Expect(err).NotTo(HaveOccurred())

	g.Expect(verifyToken(t, first.JWS, signer.PublicKey()).Claims.ID).
		NotTo(Equal(verifyToken(t, second.JWS, signer.PublicKey()).Claims.ID))
}

func TestMint_HeaderIsCompactJWS(t *testing.T) {
	g := NewWithT(t)

	signer, err := NewFakeSigner("app-token-signer-")
	g.Expect(err).NotTo(HaveOccurred())

	token, err := Mint(context.Background(), signer, opclock.NewFakeClock(), "ttd", "app", testConfig())
	g.Expect(err).NotTo(HaveOccurred())

	parts := strings.Split(token.JWS, ".")
	g.Expect(parts).To(HaveLen(3))
	header, err := base64.RawURLEncoding.DecodeString(parts[0])
	g.Expect(err).NotTo(HaveOccurred())
	g.Expect(header).To(MatchJSON(`{"alg":"ES256","kid":"local","typ":"JWT"}`))
	signature, err := base64.RawURLEncoding.DecodeString(parts[2])
	g.Expect(err).NotTo(HaveOccurred())
	g.Expect(signature).To(HaveLen(es256SignatureLength))
}

func TestMint_ReturnsSignerError(t *testing.T) {
	g := NewWithT(t)

	signer, err := NewFakeSigner("app-token-signer-")
	g.Expect(err).NotTo(HaveOccurred())
	signer.SetError(errSimulatedSigner)

	_, err = Mint(context.Background(), signer, opclock.NewFakeClock(), "ttd", "app", testConfig())
	g.Expect(err).To(MatchError(errSimulatedSigner))
}
