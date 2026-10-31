// Package apptokensync keeps a valid app token in each app's deployment secret. Apps present the
// token to Storage, and reload the file when it changes.
package apptokensync

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"

	commontelemetry "altinn.studio/common/telemetry"

	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/builder"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/event"
	"sigs.k8s.io/controller-runtime/pkg/log"
	"sigs.k8s.io/controller-runtime/pkg/predicate"

	"altinn.studio/operator/internal/apptoken"
	"altinn.studio/operator/internal/config"
	rt "altinn.studio/operator/internal/runtime"
	"altinn.studio/operator/internal/telemetry"
)

const (
	controllerName      = "apptokensync"
	appSecretNamespace  = "default"
	appSecretNameSuffix = "-deployment-secrets"
	appTokenFileName    = "app-token.json" //nolint:gosec // A file name, not a credential.

	// signerErrorBackoff is the retry delay when a token cannot be signed. The previous token stays in
	// place, and with the default timings it is valid for at least another hour.
	signerErrorBackoff = time.Minute
	maxUpdateRetries   = 3
)

var errSecretUpdateRetryExhausted = errors.New("failed to update secret after conflict retries")

// appTokenFile is the app-token.json file apps read.
type appTokenFile struct {
	AppToken *appTokenEntry `json:"AppToken"`
}

type appTokenEntry struct {
	Token     string `json:"Token"`
	IssuedAt  string `json:"IssuedAt"`
	ExpiresAt string `json:"ExpiresAt"`
	RefreshAt string `json:"RefreshAt"`
}

type AppTokenSyncReconciler struct {
	runtime   rt.Runtime
	k8sClient client.Client
	signer    apptoken.Signer
}

// +kubebuilder:rbac:groups="",resources=secrets,verbs=get;list;watch;update

func NewReconciler(runtime rt.Runtime, k8sClient client.Client, signer apptoken.Signer) *AppTokenSyncReconciler {
	return &AppTokenSyncReconciler{
		k8sClient: k8sClient,
		runtime:   runtime,
		signer:    signer,
	}
}

func (r *AppTokenSyncReconciler) Reconcile(ctx context.Context, req ctrl.Request) (ctrl.Result, error) {
	ctx, span := telemetry.StartRun(ctx, controllerName, "apptokensync.Reconcile",
		telemetry.AttrObjectKind.String("Secret"),
		telemetry.AttrObjectNamespace.String(req.Namespace),
		telemetry.AttrObjectName.String(req.Name),
	)
	defer span.End()

	org := r.runtime.GetOperatorContext().ServiceOwner.Id
	app, ok := appNameFromSecret(req.Namespace, req.Name, org)
	if !ok {
		return ctrl.Result{}, nil
	}

	secret := &corev1.Secret{}
	if err := r.k8sClient.Get(ctx, req.NamespacedName, secret); err != nil {
		if apierrors.IsNotFound(err) {
			return ctrl.Result{}, nil
		}
		commontelemetry.SetError(span, "get_secret", err)
		return ctrl.Result{}, fmt.Errorf("get secret: %w", err)
	}

	cfg := r.runtime.GetConfigMonitor().Get().AppTokenController
	now := r.runtime.GetClock().Now().UTC()

	if refreshAt, ok := currentRefreshAt(secret, cfg, now); ok && now.Before(refreshAt) {
		return ctrl.Result{RequeueAfter: refreshAt.Sub(now)}, nil
	}

	token, err := apptoken.Mint(ctx, r.signer, r.runtime.GetClock(), org, app, cfg)
	if err != nil {
		// Keep the current file: its token may still be valid, and apps keep using it.
		commontelemetry.SetError(span, "mint_app_token", err)
		log.FromContext(ctx).
			Error(err, "failed to mint app token, retrying", "app", app, "retryAfter", signerErrorBackoff)
		return ctrl.Result{RequeueAfter: signerErrorBackoff}, nil
	}

	refreshAt := token.ExpiresAt.Add(-cfg.RefreshLeadTime)
	content, err := json.Marshal(appTokenFile{AppToken: &appTokenEntry{
		Token:     token.JWS,
		IssuedAt:  token.IssuedAt.Format(time.RFC3339),
		ExpiresAt: token.ExpiresAt.Format(time.RFC3339),
		RefreshAt: refreshAt.Format(time.RFC3339),
	}})
	if err != nil {
		commontelemetry.SetError(span, "marshal_app_token", err)
		return ctrl.Result{}, fmt.Errorf("marshal app token file: %w", err)
	}

	if err := r.updateSecretWithRetry(ctx, secret, content); err != nil {
		commontelemetry.SetError(span, "update_secret", err)
		return ctrl.Result{}, err
	}

	return ctrl.Result{RequeueAfter: refreshAt.Sub(now)}, nil
}

// currentRefreshAt returns when the token in the secret should be replaced, or false when the
// secret has no usable token. The refresh time is derived from the expiry and the current config,
// so a change to refresh_lead_time applies to existing tokens.
func currentRefreshAt(secret *corev1.Secret, cfg config.AppTokenControllerConfig, now time.Time) (time.Time, bool) {
	content := secret.Data[appTokenFileName]
	if len(content) == 0 {
		return time.Time{}, false
	}

	var file appTokenFile
	if err := json.Unmarshal(content, &file); err != nil || file.AppToken == nil || file.AppToken.Token == "" {
		return time.Time{}, false
	}

	expiresAt, err := time.Parse(time.RFC3339, file.AppToken.ExpiresAt)
	if err != nil {
		return time.Time{}, false
	}
	// An expiry further out than a new token's would get is not one this controller wrote.
	if expiresAt.After(now.Add(cfg.TokenLifetime)) {
		return time.Time{}, false
	}

	return expiresAt.Add(-cfg.RefreshLeadTime), true
}

func (r *AppTokenSyncReconciler) SetupWithManager(mgr ctrl.Manager) error {
	if err := ctrl.NewControllerManagedBy(mgr).
		Named(controllerName).
		For(&corev1.Secret{}, builder.WithPredicates(r.secretPredicate())).
		Complete(r); err != nil {
		return fmt.Errorf("complete AppTokenSync controller builder: %w", err)
	}
	return nil
}

func (r *AppTokenSyncReconciler) secretPredicate() predicate.Predicate {
	isTarget := func(obj client.Object) bool {
		_, ok := appNameFromSecret(obj.GetNamespace(), obj.GetName(), r.runtime.GetOperatorContext().ServiceOwner.Id)
		return ok
	}
	return predicate.Funcs{
		CreateFunc:  func(e event.CreateEvent) bool { return isTarget(e.Object) },
		UpdateFunc:  func(e event.UpdateEvent) bool { return isTarget(e.ObjectNew) },
		DeleteFunc:  func(event.DeleteEvent) bool { return false },
		GenericFunc: func(e event.GenericEvent) bool { return isTarget(e.Object) },
	}
}

// appNameFromSecret returns the app of the secret {org}-{app}-deployment-secrets in the default
// namespace. App names may contain dashes.
func appNameFromSecret(namespace, name, org string) (string, bool) {
	if namespace != appSecretNamespace {
		return "", false
	}
	app, ok := strings.CutPrefix(name, org+"-")
	if !ok {
		return "", false
	}
	app, ok = strings.CutSuffix(app, appSecretNameSuffix)
	if !ok || app == "" {
		return "", false
	}
	return app, true
}

func (r *AppTokenSyncReconciler) updateSecretWithRetry(
	ctx context.Context,
	secret *corev1.Secret,
	content []byte,
) error {
	for attempt := range maxUpdateRetries {
		updatedSecret := secret.DeepCopy()
		if updatedSecret.Data == nil {
			updatedSecret.Data = make(map[string][]byte)
		}
		updatedSecret.Data[appTokenFileName] = content

		err := r.k8sClient.Update(ctx, updatedSecret)
		if err == nil {
			log.FromContext(ctx).Info("updated app token", "name", secret.Name, "namespace", secret.Namespace)
			return nil
		}
		if !apierrors.IsConflict(err) {
			return fmt.Errorf("update secret: %w", err)
		}

		log.FromContext(ctx).Info(
			"conflict updating app secret, retrying",
			"name", secret.Name,
			"namespace", secret.Namespace,
			"attempt", attempt+1,
		)
		if err := r.k8sClient.Get(ctx, client.ObjectKeyFromObject(secret), secret); err != nil {
			return fmt.Errorf("refresh secret: %w", err)
		}
	}

	return fmt.Errorf("%w: %s/%s", errSecretUpdateRetryExhausted, secret.Namespace, secret.Name)
}
