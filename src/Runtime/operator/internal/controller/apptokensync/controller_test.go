package apptokensync

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"testing"
	"time"

	"github.com/go-jose/go-jose/v4"
	"github.com/go-jose/go-jose/v4/jwt"
	. "github.com/onsi/gomega"
	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	k8sruntime "k8s.io/apimachinery/pkg/runtime"
	"k8s.io/apimachinery/pkg/runtime/schema"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/client/fake"
	"sigs.k8s.io/controller-runtime/pkg/client/interceptor"

	"altinn.studio/operator/internal"
	"altinn.studio/operator/internal/apptoken"
	opclock "altinn.studio/operator/internal/clock"
	"altinn.studio/operator/internal/operatorcontext"
)

var (
	errSimulatedConflict = errors.New("simulated conflict")
	errSimulatedSigner   = errors.New("simulated signer failure")
)

var startTime = time.Date(2026, 3, 23, 12, 0, 0, 0, time.UTC)

// The timings in localtest.env: 2h lifetime, 1h refresh lead.
const (
	tokenLifetime   = 2 * time.Hour
	refreshLeadTime = time.Hour
)

type testHarness struct {
	reconciler *AppTokenSyncReconciler
	k8sClient  client.Client
	clock      *opclock.FakeClock
	signer     *apptoken.FakeSigner
}

type tokenClaims struct {
	jwt.Claims

	Org string `json:"urn:altinn:org"`
	App string `json:"urn:altinn:app"`
}

func newFakeK8sClient(interceptorFuncs interceptor.Funcs, initObjs ...client.Object) client.Client {
	scheme := k8sruntime.NewScheme()
	if err := corev1.AddToScheme(scheme); err != nil {
		panic(err)
	}
	return fake.NewClientBuilder().
		WithScheme(scheme).
		WithInterceptorFuncs(interceptorFuncs).
		WithObjects(initObjs...).
		Build()
}

func newTestHarness(t *testing.T, initObjs ...client.Object) *testHarness {
	t.Helper()
	return newTestHarnessWithClient(t, newFakeK8sClient(interceptor.Funcs{}, initObjs...))
}

func newTestHarnessWithClient(t *testing.T, k8sClient client.Client) *testHarness {
	t.Helper()

	clock := opclock.NewFakeClockAt(startTime)
	rt, err := internal.NewRuntime(
		context.Background(),
		internal.WithClock(clock),
		internal.WithOperatorContext(&operatorcontext.Context{
			Environment:  operatorcontext.EnvironmentLocal,
			ServiceOwner: operatorcontext.ServiceOwner{Id: "ttd"},
		}),
	)
	if err != nil {
		t.Fatalf("failed to create runtime: %v", err)
	}

	signer, err := apptoken.NewFakeSigner("app-token-signer-")
	if err != nil {
		t.Fatalf("failed to create signer: %v", err)
	}

	return &testHarness{
		reconciler: NewReconciler(rt, k8sClient, signer),
		k8sClient:  k8sClient,
		clock:      clock,
		signer:     signer,
	}
}

func (h *testHarness) reconcile(t *testing.T, key client.ObjectKey) ctrl.Result {
	t.Helper()
	result, err := h.reconciler.Reconcile(context.Background(), ctrl.Request{NamespacedName: key})
	if err != nil {
		t.Fatalf("reconcile failed: %v", err)
	}
	return result
}

func (h *testHarness) secret(t *testing.T, key client.ObjectKey) *corev1.Secret {
	t.Helper()
	secret := &corev1.Secret{}
	if err := h.k8sClient.Get(context.Background(), key, secret); err != nil {
		t.Fatalf("get secret: %v", err)
	}
	return secret
}

func (h *testHarness) tokenFile(t *testing.T, key client.ObjectKey) []byte {
	t.Helper()
	content, ok := h.secret(t, key).Data[appTokenFileName]
	if !ok {
		t.Fatalf("secret %s has no %s", key, appTokenFileName)
	}
	return content
}

func appSecret(name string, data map[string][]byte) *corev1.Secret {
	return &corev1.Secret{
		ObjectMeta: metav1.ObjectMeta{Name: name, Namespace: "default"},
		Data:       data,
	}
}

func parseTokenFile(t *testing.T, content []byte) appTokenEntry {
	t.Helper()
	var file appTokenFile
	if err := json.Unmarshal(content, &file); err != nil {
		t.Fatalf("unmarshal token file: %v", err)
	}
	if file.AppToken == nil {
		t.Fatal("token file has no AppToken")
	}
	return *file.AppToken
}

func verifyToken(t *testing.T, h *testHarness, token string) (jose.Header, tokenClaims) {
	t.Helper()
	parsed, err := jwt.ParseSigned(token, []jose.SignatureAlgorithm{jose.ES256})
	if err != nil {
		t.Fatalf("parse token: %v", err)
	}
	var claims tokenClaims
	if err := parsed.Claims(h.signer.PublicKey(), &claims); err != nil {
		t.Fatalf("verify token: %v", err)
	}
	return parsed.Headers[0], claims
}

func TestReconciler_IssuesTokenOnFirstRun(t *testing.T) {
	g := NewWithT(t)

	target := appSecret("ttd-my-dashed-app-deployment-secrets", map[string][]byte{"existing-key": []byte("value")})
	other := appSecret("nhn-my-dashed-app-deployment-secrets", nil)
	h := newTestHarness(t, target, other)
	h.signer.SetVersion("4f1c0a2b9e")

	result := h.reconcile(t, client.ObjectKeyFromObject(target))
	g.Expect(result.RequeueAfter).To(Equal(tokenLifetime - refreshLeadTime))

	secret := h.secret(t, client.ObjectKeyFromObject(target))
	g.Expect(secret.Data).To(HaveKeyWithValue("existing-key", []byte("value")))

	var raw map[string]map[string]any
	g.Expect(json.Unmarshal(secret.Data[appTokenFileName], &raw)).To(Succeed())
	g.Expect(raw).To(HaveKey("AppToken"))
	g.Expect(raw["AppToken"]).To(HaveLen(4))
	g.Expect(raw["AppToken"]).To(And(
		HaveKey("Token"),
		HaveKeyWithValue("IssuedAt", "2026-03-23T12:00:00Z"),
		HaveKeyWithValue("ExpiresAt", "2026-03-23T14:00:00Z"),
		HaveKeyWithValue("RefreshAt", "2026-03-23T13:00:00Z"),
	))

	entry := parseTokenFile(t, secret.Data[appTokenFileName])
	header, claims := verifyToken(t, h, entry.Token)
	g.Expect(header.KeyID).To(Equal("4f1c0a2b9e"))
	g.Expect(header.Algorithm).To(Equal(string(jose.ES256)))
	g.Expect(claims.Issuer).To(Equal("altinn-studio-operator"))
	g.Expect(claims.Audience).To(Equal(jwt.Audience{"altinn-storage"}))
	g.Expect(claims.Org).To(Equal("ttd"))
	g.Expect(claims.App).To(Equal("ttd/my-dashed-app"))
	g.Expect(claims.IssuedAt.Time().UTC()).To(Equal(startTime))
	g.Expect(claims.NotBefore.Time().UTC()).To(Equal(startTime))
	g.Expect(claims.Expiry.Time().UTC()).To(Equal(startTime.Add(tokenLifetime)))
	g.Expect(claims.ID).NotTo(BeEmpty())

	g.Expect(h.secret(t, client.ObjectKeyFromObject(other)).Data).NotTo(HaveKey(appTokenFileName))
}

func TestReconciler_KeepsTokenUntilRefreshAt(t *testing.T) {
	g := NewWithT(t)

	target := appSecret("ttd-app-deployment-secrets", nil)
	h := newTestHarness(t, target)
	key := client.ObjectKeyFromObject(target)

	h.reconcile(t, key)
	issued := h.tokenFile(t, key)

	h.clock.Advance(59 * time.Minute)
	result := h.reconcile(t, key)

	g.Expect(h.tokenFile(t, key)).To(Equal(issued))
	g.Expect(result.RequeueAfter).To(Equal(time.Minute))
}

func TestReconciler_ReplacesTokenAtRefreshAt(t *testing.T) {
	g := NewWithT(t)

	target := appSecret("ttd-app-deployment-secrets", nil)
	h := newTestHarness(t, target)
	key := client.ObjectKeyFromObject(target)

	h.reconcile(t, key)
	issued := parseTokenFile(t, h.tokenFile(t, key))

	h.clock.Advance(tokenLifetime - refreshLeadTime)
	h.signer.SetVersion("next")
	result := h.reconcile(t, key)

	replaced := parseTokenFile(t, h.tokenFile(t, key))
	g.Expect(replaced.Token).NotTo(Equal(issued.Token))
	g.Expect(replaced.IssuedAt).To(Equal("2026-03-23T13:00:00Z"))
	g.Expect(replaced.ExpiresAt).To(Equal("2026-03-23T15:00:00Z"))
	g.Expect(replaced.RefreshAt).To(Equal("2026-03-23T14:00:00Z"))
	g.Expect(result.RequeueAfter).To(Equal(tokenLifetime - refreshLeadTime))

	header, _ := verifyToken(t, h, replaced.Token)
	g.Expect(header.KeyID).To(Equal("next"))
}

func TestReconciler_SignerErrorKeepsPreviousTokenAndRequeues(t *testing.T) {
	g := NewWithT(t)

	target := appSecret("ttd-app-deployment-secrets", nil)
	h := newTestHarness(t, target)
	key := client.ObjectKeyFromObject(target)

	h.reconcile(t, key)
	issued := h.tokenFile(t, key)

	h.clock.Advance(tokenLifetime - refreshLeadTime)
	h.signer.SetError(errSimulatedSigner)
	result := h.reconcile(t, key)

	g.Expect(result.RequeueAfter).To(Equal(signerErrorBackoff))
	g.Expect(h.tokenFile(t, key)).To(Equal(issued))

	h.signer.SetError(nil)
	h.clock.Advance(signerErrorBackoff)
	h.reconcile(t, key)
	g.Expect(h.tokenFile(t, key)).NotTo(Equal(issued))
}

func TestReconciler_SignerErrorWithoutTokenWritesNothing(t *testing.T) {
	g := NewWithT(t)

	target := appSecret("ttd-app-deployment-secrets", nil)
	h := newTestHarness(t, target)
	h.signer.SetError(errSimulatedSigner)

	result := h.reconcile(t, client.ObjectKeyFromObject(target))

	g.Expect(result.RequeueAfter).To(Equal(signerErrorBackoff))
	g.Expect(h.secret(t, client.ObjectKeyFromObject(target)).Data).NotTo(HaveKey(appTokenFileName))
}

func TestReconciler_ReplacesUnusableTokenFile(t *testing.T) {
	tests := []struct {
		name    string
		content string
	}{
		{name: "not json", content: "not json"},
		{name: "no AppToken", content: `{}`},
		{name: "null AppToken", content: `{"AppToken":null}`},
		{name: "empty token", content: `{"AppToken":{"Token":"","ExpiresAt":"2026-03-23T13:30:00Z"}}`},
		{name: "unparsable expiry", content: `{"AppToken":{"Token":"x","ExpiresAt":"later"}}`},
		{name: "expired", content: `{"AppToken":{"Token":"x","ExpiresAt":"2026-03-23T11:00:00Z"}}`},
		{name: "inside refresh lead", content: `{"AppToken":{"Token":"x","ExpiresAt":"2026-03-23T12:30:00Z"}}`},
		{name: "expiry beyond lifetime", content: `{"AppToken":{"Token":"x","ExpiresAt":"2026-03-30T12:00:00Z"}}`},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			g := NewWithT(t)

			target := appSecret("ttd-app-deployment-secrets", map[string][]byte{appTokenFileName: []byte(tt.content)})
			h := newTestHarness(t, target)

			result := h.reconcile(t, client.ObjectKeyFromObject(target))

			entry := parseTokenFile(t, h.tokenFile(t, client.ObjectKeyFromObject(target)))
			g.Expect(entry.IssuedAt).To(Equal("2026-03-23T12:00:00Z"))
			verifyToken(t, h, entry.Token)
			g.Expect(result.RequeueAfter).To(Equal(tokenLifetime - refreshLeadTime))
		})
	}
}

func TestReconciler_IgnoresOtherSecrets(t *testing.T) {
	tests := []struct {
		name      string
		namespace string
	}{
		{name: "nhn-app-deployment-secrets", namespace: "default"},
		{name: "ttdx-app-deployment-secrets", namespace: "default"},
		{name: "ttd-app-other-secrets", namespace: "default"},
		{name: "ttd-deployment-secrets", namespace: "default"},
		{name: "ttd-app-deployment-secrets", namespace: "runtime-gateway"},
	}

	for _, tt := range tests {
		t.Run(tt.namespace+"/"+tt.name, func(t *testing.T) {
			g := NewWithT(t)

			secret := &corev1.Secret{ObjectMeta: metav1.ObjectMeta{Name: tt.name, Namespace: tt.namespace}}
			h := newTestHarness(t, secret)

			result := h.reconcile(t, client.ObjectKeyFromObject(secret))

			g.Expect(result).To(Equal(ctrl.Result{}))
			g.Expect(h.secret(t, client.ObjectKeyFromObject(secret)).Data).NotTo(HaveKey(appTokenFileName))
		})
	}
}

func TestAppNameFromSecret(t *testing.T) {
	tests := []struct {
		namespace string
		name      string
		want      string
		wantOK    bool
	}{
		{namespace: "default", name: "ttd-app-deployment-secrets", want: "app", wantOK: true},
		{namespace: "default", name: "ttd-my-dashed-app-deployment-secrets", want: "my-dashed-app", wantOK: true},
		{namespace: "default", name: "ttd-ttd-deployment-secrets", want: "ttd", wantOK: true},
		{namespace: "default", name: "ttd--deployment-secrets"},
		{namespace: "default", name: "ttd-deployment-secrets"},
		{namespace: "default", name: "nhn-app-deployment-secrets"},
		{namespace: "default", name: "ttdx-app-deployment-secrets"},
		{namespace: "other", name: "ttd-app-deployment-secrets"},
	}

	for _, tt := range tests {
		t.Run(tt.namespace+"/"+tt.name, func(t *testing.T) {
			g := NewWithT(t)
			app, ok := appNameFromSecret(tt.namespace, tt.name, "ttd")
			g.Expect(ok).To(Equal(tt.wantOK))
			g.Expect(app).To(Equal(tt.want))
		})
	}
}

func TestReconciler_RetriesOnConflictAndPreservesConcurrentChanges(t *testing.T) {
	g := NewWithT(t)

	target := appSecret("ttd-app-deployment-secrets", map[string][]byte{"existing-key": []byte("before")})
	conflictInjected := false
	k8sClient := newFakeK8sClient(
		interceptor.Funcs{
			Update: func(ctx context.Context, c client.WithWatch, obj client.Object, opts ...client.UpdateOption) error {
				if conflictInjected {
					return c.Update(ctx, obj, opts...)
				}
				conflictInjected = true

				current := &corev1.Secret{}
				if err := c.Get(ctx, client.ObjectKeyFromObject(obj), current); err != nil {
					return fmt.Errorf("get secret during conflict injection: %w", err)
				}
				current.Data["existing-key"] = []byte("after-conflict")
				if err := c.Update(ctx, current); err != nil {
					return fmt.Errorf("update secret during conflict injection: %w", err)
				}

				return apierrors.NewConflict(
					schema.GroupResource{Group: "", Resource: "secrets"},
					obj.GetName(),
					errSimulatedConflict,
				)
			},
		},
		target,
	)
	h := newTestHarnessWithClient(t, k8sClient)

	h.reconcile(t, client.ObjectKeyFromObject(target))

	secret := h.secret(t, client.ObjectKeyFromObject(target))
	g.Expect(conflictInjected).To(BeTrue())
	g.Expect(secret.Data).To(HaveKeyWithValue("existing-key", []byte("after-conflict")))
	g.Expect(secret.Data).To(HaveKey(appTokenFileName))
}
