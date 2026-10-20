package localtestv2

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"time"

	"altinn.studio/devenv/pkg/container"
	containertypes "altinn.studio/devenv/pkg/container/types"
	"altinn.studio/devenv/pkg/resource"
	"altinn.studio/devenv/pkg/resource/executor"
	containerbackend "altinn.studio/devenv/pkg/resource/executor/container"
	envtypes "altinn.studio/studioctl/internal/cmd/env"
	envlocaltest "altinn.studio/studioctl/internal/cmd/env/localtest"
	v1components "altinn.studio/studioctl/internal/cmd/env/localtest/components"
	"altinn.studio/studioctl/internal/config"
	"altinn.studio/studioctl/internal/envtopology"
	"altinn.studio/studioctl/internal/localtestv2"
	"altinn.studio/studioctl/internal/osutil"
	"altinn.studio/studioctl/internal/studioctlserver"
	"altinn.studio/studioctl/internal/ui"
	"altinn.studio/studioctl/internal/ui/resourcegraph"
)

const (
	// Name is the runtime's name on the command line (-r localtest-v2).
	Name = "localtest-v2"

	// EnvSourceRoot points at the research checkout's localtest/ folder, which `env up --dev <component>`
	// builds component images from.
	EnvSourceRoot = "STUDIOCTL_LOCALTEST_V2_SRC"

	// graphID scopes devenv ownership labels to this runtime's graph.
	graphID = "studioctl-localtest-v2"

	teardownTimeout = 30 * time.Second
	stoppingMessage = "Stopping localtest-v2 environment..."
)

var (
	// ErrLocaltestRunning is returned when the localtest runtime holds the public origin's port.
	ErrLocaltestRunning    = errors.New("the localtest runtime is running; stop it first with 'env down'")
	errUnknownDevComponent = errors.New("unknown component for --dev")
	errDevSourceMissing    = errors.New(
		"--dev needs " + EnvSourceRoot + " to point at the research checkout's localtest/ folder",
	)
)

// Env implements envtypes.Env for the localtest-v2 runtime.
type Env struct {
	cfg    *config.Config
	out    *ui.Output
	client container.ContainerClient
}

// NewEnv creates the localtest-v2 environment manager.
func NewEnv(cfg *config.Config, out *ui.Output, client container.ContainerClient) *Env {
	return &Env{cfg: cfg, out: out, client: client}
}

// Name returns the runtime name.
func (e *Env) Name() string {
	return Name
}

// Preflight validates prerequisites before startup: the localtest runtime must not hold port 8000, and a
// dev build needs the research checkout.
func (e *Env) Preflight(ctx context.Context, opts envtypes.UpOptions) error {
	if err := envlocaltest.CheckForLegacyLocaltest(ctx, e.client); err != nil {
		return fmt.Errorf("check for legacy localtest: %w", err)
	}
	info, err := e.client.ContainerInspect(ctx, v1components.ContainerLocaltest)
	if err == nil && info.State.Running {
		return ErrLocaltestRunning
	}
	if err != nil && !errors.Is(err, container.ErrContainerNotFound) {
		return fmt.Errorf("inspect container %s: %w", v1components.ContainerLocaltest, err)
	}
	if _, err := devComponents(opts.DevComponents); err != nil {
		return err
	}
	return nil
}

// Up starts the environment: writes the certificate and the proxy's route table, (re)starts
// studioctl-server as the proxy, applies the containers, and waits until every component answers.
func (e *Env) Up(ctx context.Context, upOpts envtypes.UpOptions) error {
	toolchain := e.client.Toolchain()
	e.out.Verbosef("Using container toolchain: %s via %s", toolchain.Platform, toolchain.AccessMode)

	opts, err := e.options(upOpts)
	if err != nil {
		return err
	}
	if err := ensureCertificate(opts.CertDir); err != nil {
		return err
	}
	if err := envtopology.WriteBoundTopologyConfig(
		e.cfg.BoundTopologyBaseConfigPath(),
		boundTopologyConfig(opts),
	); err != nil {
		return fmt.Errorf("write proxy routes: %w", err)
	}
	if err := localtestv2.WriteState(e.cfg, localtestv2.State{
		CertificateDir: opts.CertDir,
		ProxyPorts:     studioctlserver.LocaltestV2ProxyPorts(opts.Topology.IngressPort()),
	}); err != nil {
		return fmt.Errorf("record localtest-v2 state: %w", err)
	}
	if err := studioctlserver.EnsureStarted(ctx, e.cfg, opts.Topology.IngressPort()); err != nil {
		return fmt.Errorf("ensure studioctl-server: %w", err)
	}

	if err := e.applyResources(ctx, resources(opts), len(opts.DevComponents) > 0); err != nil {
		return err
	}
	e.out.Println("Waiting for the components to answer...")
	if err := waitHealthy(ctx, e.out, healthTargets(opts)); err != nil {
		return fmt.Errorf("localtest-v2 did not become healthy: %w", err)
	}
	e.out.Success("Environment healthy")

	localtestURL := opts.Topology.LocaltestURL()
	if upOpts.OpenBrowser {
		if err := osutil.OpenContext(ctx, localtestURL); err != nil {
			e.out.Warningf("Failed to open browser: %v", err)
		}
	}
	if !upOpts.Detach {
		return e.runForeground(ctx, localtestURL, opts)
	}
	e.out.Println("")
	e.out.Println("localtest-v2 started in background.")
	e.out.Printlnf("Access the platform at: %s", localtestURL)
	e.out.Printlnf("Use '%s env logs -r %s' to view logs.", osutil.CurrentBin(), Name)
	e.out.Printlnf("Use '%s env down -r %s' to stop.", osutil.CurrentBin(), Name)
	return nil
}

// Down stops the environment and takes studioctl-server out of proxy mode.
func (e *Env) Down(ctx context.Context) error {
	hasResources, err := e.hasManagedResources(ctx)
	if err != nil {
		return err
	}
	if !hasResources {
		return e.forgetState(ctx, envtypes.ErrAlreadyStopped)
	}
	if err := e.destroyResources(ctx, resources(e.defaultOptions()), stoppingMessage); err != nil {
		return fmt.Errorf("stop environment: %w", err)
	}
	if err := e.forgetState(ctx, nil); err != nil {
		return err
	}
	e.out.Success("Environment stopped")
	return nil
}

// Reset stops the environment if needed and deletes its volumes.
func (e *Env) Reset(ctx context.Context) error {
	hasResources, err := e.hasManagedResources(ctx)
	if err != nil {
		return err
	}
	if hasResources {
		if err := e.destroyResources(ctx, resources(e.defaultOptions()), stoppingMessage); err != nil {
			return fmt.Errorf("stop environment: %w", err)
		}
	}
	if err := e.forgetState(ctx, nil); err != nil {
		return err
	}
	e.out.Println("Deleting persisted localtest-v2 data...")
	for _, volume := range []string{volumePostgresData, volumeStorageBlobs, volumePolicyStore, volumeLocaltestData} {
		if err := e.client.VolumeRemove(
			ctx,
			volume,
			true,
		); err != nil &&
			!errors.Is(err, containertypes.ErrVolumeNotFound) {
			return fmt.Errorf("remove volume %q: %w", volume, err)
		}
	}
	e.out.Success("Environment data reset")
	return nil
}

// Logs streams the containers' logs.
func (e *Env) Logs(ctx context.Context, opts envtypes.LogsOptions) error {
	streamer := envlocaltest.NewLogStreamer(e.client, e.out, containerNames(e.defaultOptions()))
	component := opts.Component
	if component != "" && !strings.HasPrefix(component, containerPrefix) {
		component = containerPrefix + component
	}
	if err := streamer.Stream(ctx, component, opts.Follow, opts.JSON); err != nil {
		return fmt.Errorf("stream logs: %w", err)
	}
	return nil
}

// Status returns the environment's container status.
func (e *Env) Status(ctx context.Context) (*envlocaltest.Status, error) {
	graph, err := buildResourceGraph(resources(e.defaultOptions()))
	if err != nil {
		return nil, err
	}
	exec, err := newResourceExecutor(e.client)
	if err != nil {
		return nil, err
	}
	snapshot, err := exec.Status(ctx, graph, executor.SkipResource(isImageResource))
	if err != nil {
		return nil, fmt.Errorf("get resource status: %w", err)
	}

	status := envlocaltest.Status{Containers: []envlocaltest.ContainerStatus{}, Running: false, AnyRunning: false}
	ready := 0
	for _, res := range graph.All() {
		ctr, ok := res.(*resource.Container)
		if !ok {
			continue
		}
		resourceStatus, imageID := observedContainer(snapshot, ctr)
		status.Containers = append(status.Containers, envlocaltest.ContainerStatus{
			Name:    ctr.Name,
			Image:   e.imageRef(ctx, ctr, imageID),
			ImageID: imageID,
			Status:  statusString(resourceStatus),
		})
		if resourceStatus.IsHealthy() {
			ready++
		}
		if resourceStatus != executor.StatusDestroyed {
			status.AnyRunning = true
		}
	}
	status.Running = len(status.Containers) > 0 && ready == len(status.Containers)
	return &status, nil
}

// observedContainer reads a container's status and image from the snapshot; a container studioctl does
// not own counts as absent.
func observedContainer(snapshot executor.Snapshot, ctr *resource.Container) (executor.Status, string) {
	observed, present := snapshot.Resources[ctr.ID()]
	if !present || !observed.Managed {
		return executor.StatusDestroyed, ""
	}
	return observed.Status, observed.ImageID
}

// imageRef names the reference a container was pulled from, when that reference still resolves to the
// build the container runs; a dev build or a moved tag has none.
func (e *Env) imageRef(ctx context.Context, ctr *resource.Container, imageID string) string {
	pulled, isPulled := ctr.Image.Resource().(*resource.PulledImage)
	if !isPulled || imageID == "" {
		return ""
	}
	info, err := e.client.ImageInspect(ctx, pulled.Ref)
	if err != nil || info.ID != imageID {
		return ""
	}
	return pulled.Ref
}

func statusString(status executor.Status) string {
	if status == executor.StatusDestroyed {
		return "not found"
	}
	if status == executor.StatusReady {
		return "running"
	}
	return status.String()
}

// forgetState removes the runtime's state and, when studioctl-server runs, restarts it without the proxy.
// The passthrough error is returned when the state is gone, so a caller can report "already stopped".
func (e *Env) forgetState(ctx context.Context, passthrough error) error {
	if err := localtestv2.RemoveState(e.cfg); err != nil {
		return fmt.Errorf("forget localtest-v2 state: %w", err)
	}
	if err := studioctlserver.ReconcileIfRunning(ctx, e.cfg, envtopology.DefaultIngressPortString()); err != nil {
		return fmt.Errorf("reconcile studioctl-server: %w", err)
	}
	return passthrough
}

func (e *Env) defaultOptions() *Options {
	return defaultOptions(e.cfg.Images, localtestv2.CertificateDir(e.cfg))
}

func (e *Env) options(upOpts envtypes.UpOptions) (*Options, error) {
	opts := e.defaultOptions()
	opts.RuntimeUser, opts.UsernsMode, opts.RelabelBinds = v1components.RuntimeUser(e.client.Toolchain())
	if upOpts.LatestImages {
		opts.Images.LocaltestV2.UseTag("latest")
	}
	dev, err := devComponents(upOpts.DevComponents)
	if err != nil {
		return nil, err
	}
	if len(dev) > 0 {
		root := filepath.Clean(strings.TrimSpace(os.Getenv(EnvSourceRoot)))
		if root == "" || root == "." {
			return nil, errDevSourceMissing
		}
		if _, err := os.Stat(root); err != nil {
			return nil, fmt.Errorf("%w: %s: %w", errDevSourceMissing, root, err)
		}
		opts.DevSourceRoot = root
		opts.DevComponents = dev
	}
	return opts, nil
}

// devComponents validates --dev names; "all" selects every component that has a dev build.
func devComponents(names []string) (map[string]bool, error) {
	selected := map[string]bool{}
	buildable := DevBuildable()
	for _, name := range names {
		if name == "all" {
			for _, b := range buildable {
				selected[b] = true
			}
			continue
		}
		if !slices.Contains(buildable, name) {
			return nil, fmt.Errorf(
				"%w: %s (one of %s, all)",
				errUnknownDevComponent,
				name,
				strings.Join(buildable, ", "),
			)
		}
		selected[name] = true
	}
	return selected, nil
}

func (e *Env) runForeground(ctx context.Context, localtestURL string, opts *Options) error {
	e.out.Println("")
	e.out.Println("localtest-v2 is running. Press Ctrl+C to stop.")
	e.out.Printlnf("Access the platform at: %s", localtestURL)

	streamer := envlocaltest.NewLogStreamer(e.client, e.out, containerNames(opts))
	if err := streamer.Stream(ctx, "", true, false); err != nil {
		e.out.Verbosef("log streaming ended: %v", err)
	}
	e.out.Println("")
	e.out.Println(stoppingMessage)

	teardownCtx, cancel := context.WithTimeout(context.Background(), teardownTimeout)
	defer cancel()
	//nolint:contextcheck // intentionally using new context for cleanup after cancellation
	if err := e.destroyResources(teardownCtx, resources(opts), ""); err != nil {
		e.out.Warningf("Failed to stop environment cleanly: %v", err)
		return err
	}
	//nolint:contextcheck // same cleanup context
	if err := e.forgetState(teardownCtx, nil); err != nil {
		return err
	}
	e.out.Println("Environment stopped.")
	return nil
}

func (e *Env) hasManagedResources(ctx context.Context) (bool, error) {
	graph, err := buildResourceGraph(resources(e.defaultOptions()))
	if err != nil {
		return false, err
	}
	exec, err := newResourceExecutor(e.client)
	if err != nil {
		return false, err
	}
	snapshot, err := exec.Status(ctx, graph, executor.SkipResource(isImageResource))
	if err != nil {
		return false, fmt.Errorf("get resource status: %w", err)
	}
	for _, observed := range snapshot.Resources {
		if !observed.Managed || observed.Status == executor.StatusDestroyed {
			continue
		}
		return true, nil
	}
	return false, nil
}

func (e *Env) applyResources(ctx context.Context, resources []resource.Resource, building bool) error {
	graph, err := buildResourceGraph(resources)
	if err != nil {
		return err
	}
	exec, err := newResourceExecutor(e.client)
	if err != nil {
		return err
	}
	spinnerMsg := "Starting localtest-v2 environment..."
	if building {
		spinnerMsg = "Building and starting localtest-v2 environment..."
	}

	var renderer resourcegraph.Renderer
	outputs, err := exec.Apply(ctx, graph, executor.WithApplyPlan(func(plan executor.ApplyPlan) error {
		planned := make([]executor.PlannedResource, 0, len(plan.Destroy)+len(plan.Reconcile))
		planned = append(planned, plan.Destroy...)
		planned = append(planned, plan.Reconcile...)
		e.startRenderer(exec, &renderer, planned, resourcegraph.OperationApply, plan.Snapshot.Statuses(), spinnerMsg)
		return nil
	}))
	if err != nil {
		if renderer != nil {
			renderer.FailAll(err.Error())
			renderer.Stop()
		}
		return fmt.Errorf("start environment: %w", err)
	}
	if renderer != nil {
		renderer.Stop()
	}
	for _, res := range resources {
		image, ok := res.(*resource.PulledImage)
		if !ok {
			continue
		}
		if output, found := outputs.Image(image.ID()); found && output.Stale {
			e.out.Warningf("Could not reach the registry for %s; using the copy already on this machine.", image.Ref)
		}
	}
	e.out.Success("Containers started")
	return nil
}

func (e *Env) destroyResources(ctx context.Context, resources []resource.Resource, logStartMessage string) error {
	graph, err := buildResourceGraph(resources)
	if err != nil {
		return err
	}
	exec, err := newResourceExecutor(e.client)
	if err != nil {
		return err
	}
	var renderer resourcegraph.Renderer
	if err := exec.Destroy(ctx, graph, executor.WithDestroyPlan(func(plan executor.DestroyPlan) error {
		e.startRenderer(
			exec,
			&renderer,
			plan.Destroy,
			resourcegraph.OperationDestroy,
			plan.Snapshot.Statuses(),
			logStartMessage,
		)
		return nil
	})); err != nil {
		if renderer != nil {
			renderer.FailAll(err.Error())
			renderer.Stop()
		}
		return fmt.Errorf("destroy resources: %w", err)
	}
	if renderer != nil {
		renderer.Stop()
	}
	return nil
}

func (e *Env) startRenderer(
	exec *executor.Executor,
	renderer *resourcegraph.Renderer,
	resources []executor.PlannedResource,
	operation resourcegraph.Operation,
	statuses map[resource.ResourceID]executor.Status,
	logStartMessage string,
) {
	switch resourcegraph.DetectMode(e.out, e.cfg.Verbose) {
	case resourcegraph.ModeTable:
		*renderer = resourcegraph.NewTableWithPlan(e.out, resources, operation, statuses)
	case resourcegraph.ModeCompact:
		*renderer = resourcegraph.NewCompactWithPlan(e.out, resources, operation, statuses)
	case resourcegraph.ModeLog:
		*renderer = resourcegraph.NewLogWithPlan(e.out, resources, operation, statuses, logStartMessage)
	default:
		*renderer = resourcegraph.NewLogWithPlan(e.out, resources, operation, statuses, logStartMessage)
	}
	(*renderer).Start()
	exec.SetObserver(*renderer)
}

func newResourceExecutor(client container.ContainerClient) (*executor.Executor, error) {
	exec := executor.New()
	if err := exec.RegisterBackend(containerbackend.New(client)); err != nil {
		return nil, fmt.Errorf("register container backend: %w", err)
	}
	return exec, nil
}

func buildResourceGraph(resources []resource.Resource) (*resource.Graph, error) {
	graph := resource.NewGraph(resource.GraphID(graphID))
	for _, res := range resources {
		if err := graph.Add(res); err != nil {
			return nil, fmt.Errorf("add resource %q to graph: %w", res.ID(), err)
		}
	}
	if err := graph.Validate(); err != nil {
		return nil, fmt.Errorf("validate resource graph: %w", err)
	}
	return graph, nil
}

func isImageResource(res resource.Resource) bool {
	_, ok := res.(resource.ImageResource)
	return ok
}
