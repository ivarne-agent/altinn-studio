package localtestv2

import (
	"path/filepath"
	"slices"
	"time"

	"altinn.studio/devenv/pkg/container/types"
	"altinn.studio/devenv/pkg/resource"
	"altinn.studio/studioctl/internal/config"
	"altinn.studio/studioctl/internal/envtopology"
	"altinn.studio/studioctl/internal/localtestv2"
)

// Component names: the container is containerPrefix + name, the dev build context is components/<name> in
// the research checkout, and `env logs --component <name>` selects it.
const (
	componentPostgres         = "postgres"
	componentRegister         = "register"
	componentAuthentication   = "authentication"
	componentAuthorization    = "authorization"
	componentAccessManagement = "access-management"
	componentStorage          = "storage"
	componentEvents           = "events"
	componentLocaltest        = "localtest"
	componentPDF              = "pdf3"
	componentWorkflowEngine   = "workflow-engine"
)

// Host ports, loopback only. The proxy in studioctl-server forwards to these; 5300 is also what app-lib's
// PlatformSettings default ApiPdf2Endpoint (http://localhost:5300/pdf) names.
const (
	hostPortRegister         = "8020"
	hostPortAuthentication   = "8021"
	hostPortAuthorization    = "8022"
	hostPortAccessManagement = "8023"
	hostPortStorage          = "8024"
	hostPortEvents           = "8025"
	hostPortLocaltest        = "8026"
	hostPortWorkflowEngine   = "8028"
	hostPortPDF              = "5300"
)

// Named volumes. Each is owned by the image user that writes it (blobs uid 1654, the policy store uid 1000,
// localtest data uid 1654), which the runtime arranges on first use.
const (
	volumePostgresData  = "localtest-v2-postgres-data"
	volumeStorageBlobs  = "localtest-v2-storage-blobs"
	volumePolicyStore   = "localtest-v2-policy-store"
	volumeLocaltestData = "localtest-v2-localtest-data"
)

const (
	// networkName is the localtest-v2 container network. Containers find Postgres on it; everything else
	// they reach through the public origin on the host.
	networkName     = "localtest-v2_network"
	containerPrefix = "localtest-v2-"

	postgresAlias    = "postgres"
	postgresUser     = "postgres"
	postgresPassword = "postgres"
	postgresPort     = "5432"

	postgresHealthInterval    = 2 * time.Second
	postgresHealthTimeout     = 5 * time.Second
	postgresHealthRetries     = 30
	postgresHealthStartPeriod = 5 * time.Second

	certContainerDir  = "/certs"
	certContainerPath = certContainerDir + "/" + localtestv2.CertificateFileName
	certSettingEnv    = "AccessTokenSettings__AccessTokenSigningCertificateFullPath"

	// Containers reach the public origin, served by studioctl-server on the host, through the runtime's host
	// gateway (hostGateway maps every ingress hostname to it: the PDF generator's browser loads the app's
	// frontend assets from app-frontend.local.altinn.cloud when `studioctl run --dev-frontend` serves them).
	// Events also delivers webhooks to the developer's machine by the conventional name.
	hostGateway             = "host-gateway"
	hostDockerInternalEntry = "host.docker.internal:" + hostGateway

	frontendDevServerHostPort = "8080"
	loopback                  = "127.0.0.1"
	healthPath                = "/health"
)

// route is one public host-and-prefix the proxy forwards to a component; an empty prefix matches the whole host.
type route struct {
	host   string
	prefix string
}

// component is one container of the environment and how the proxy and the health wait reach it.
type component struct {
	healthCheck *types.HealthCheck
	env         map[string]string
	name        string
	image       config.ImageSpec
	// devContext is the build context under the research checkout for a dev build; empty when the component
	// is not built from that checkout (the PDF generator and the workflow engine come from the monorepo).
	devContext    string
	containerPort string
	hostPort      string
	// healthPath is polled on hostPort over HTTP after the containers run; empty skips the poll.
	healthPath     string
	volumes        []types.VolumeMount
	extraHosts     []string
	aliases        []string
	deps           []string
	routes         []route
	useDefaultUser bool
}

func (c component) containerName() string {
	return containerPrefix + c.name
}

func (c component) hostURL() string {
	return "http://" + loopback + ":" + c.hostPort
}

// Options configures the component set.
type Options struct {
	DevComponents map[string]bool
	CertDir       string
	DevSourceRoot string
	RuntimeUser   string
	UsernsMode    string
	Images        config.ImagesConfig
	Topology      envtopology.Local
	RelabelBinds  bool
}

// ingressHostEntries maps every public hostname to the host, where studioctl-server serves them.
func ingressHostEntries(opts *Options) []string {
	hosts := opts.Topology.LocaltestIngressHosts()
	entries := make([]string, 0, len(hosts))
	for _, host := range hosts {
		entries = append(entries, host+":"+hostGateway)
	}
	return entries
}

// newComponent is a service with its own image user and no host-writable mounts, which every component is.
func newComponent(
	opts *Options,
	name string,
	image config.ImageSpec,
	devContext, containerPort, hostPort string,
) component {
	return component{
		healthCheck:    nil,
		env:            nil,
		name:           name,
		image:          image,
		devContext:     devContext,
		containerPort:  containerPort,
		hostPort:       hostPort,
		healthPath:     "",
		volumes:        nil,
		extraHosts:     ingressHostEntries(opts),
		aliases:        nil,
		deps:           nil,
		routes:         nil,
		useDefaultUser: true,
	}
}

func containerOf(name string) string {
	return containerPrefix + name
}

// components returns the environment's containers in start order.
func components(opts *Options) []component {
	platform := opts.Topology.LocaltestBaseURL() + "/"
	return []component{
		postgresComponent(opts),
		registerComponent(opts, platform),
		authenticationComponent(opts, platform),
		authorizationComponent(opts, platform),
		storageComponent(opts, platform),
		accessManagementComponent(opts, platform),
		eventsComponent(opts, platform),
		localtestComponent(opts, platform),
		pdfComponent(opts),
		workflowEngineComponent(opts),
	}
}

func postgresComponent(opts *Options) component {
	c := newComponent(opts, componentPostgres, opts.Images.LocaltestV2.Postgres, ".", postgresPort, "")
	c.env = map[string]string{
		"POSTGRES_USER":     postgresUser,
		"POSTGRES_PASSWORD": postgresPassword,
		"TZ":                "Europe/Oslo",
	}
	c.volumes = []types.VolumeMount{namedVolume(volumePostgresData, "/var/lib/postgresql")}
	c.aliases = []string{postgresAlias}
	c.extraHosts = nil
	c.healthCheck = &types.HealthCheck{
		Test:        []string{"CMD-SHELL", "pg_isready -h 127.0.0.1 -p " + postgresPort + " -U " + postgresUser},
		Interval:    postgresHealthInterval,
		Timeout:     postgresHealthTimeout,
		Retries:     postgresHealthRetries,
		StartPeriod: postgresHealthStartPeriod,
	}
	return c
}

func registerComponent(opts *Options, platform string) component {
	c := newComponent(
		opts,
		componentRegister,
		opts.Images.LocaltestV2.Register,
		"components/register",
		"5020",
		hostPortRegister,
	)
	c.healthPath = healthPath
	c.env = map[string]string{
		certSettingEnv: certContainerPath,
		"GeneralSettings__OpenIdWellKnownEndpoint":   platform + "authentication/api/v1/openid/",
		"PlatformSettings__ApiAuthorizationEndpoint": platform + "authorization/api/v1/",
	}
	c.volumes = []types.VolumeMount{certMount(opts)}
	appHost := opts.Topology.AppHostName()
	c.routes = []route{{host: appHost, prefix: "/register"}, {host: appHost, prefix: "/profile"}}
	return c
}

func authenticationComponent(opts *Options, platform string) component {
	c := newComponent(
		opts,
		componentAuthentication,
		opts.Images.LocaltestV2.Authentication,
		"components/authentication",
		"5101",
		hostPortAuthentication,
	)
	c.healthPath = healthPath
	c.env = map[string]string{
		certSettingEnv:                      certContainerPath,
		"GeneralSettings__PlatformEndpoint": platform,
	}
	c.volumes = []types.VolumeMount{certMount(opts)}
	c.deps = []string{containerOf(componentRegister)}
	c.routes = []route{{host: opts.Topology.AppHostName(), prefix: "/authentication"}}
	return c
}

func authorizationComponent(opts *Options, platform string) component {
	c := newComponent(
		opts,
		componentAuthorization,
		opts.Images.LocaltestV2.Authorization,
		"components/authorization",
		"5070",
		hostPortAuthorization,
	)
	c.healthPath = healthPath
	c.env = map[string]string{
		certSettingEnv: certContainerPath,
		"Localtest__Authorization__PlatformEndpoint": platform,
	}
	c.volumes = []types.VolumeMount{certMount(opts), namedVolume(volumePolicyStore, "/policy-store")}
	c.deps = []string{containerOf(componentRegister)}
	c.routes = []route{{host: opts.Topology.AppHostName(), prefix: "/authorization"}}
	return c
}

func storageComponent(opts *Options, platform string) component {
	c := newComponent(
		opts,
		componentStorage,
		opts.Images.LocaltestV2.Storage,
		"components/storage",
		"5010",
		hostPortStorage,
	)
	c.healthPath = healthPath
	c.env = map[string]string{
		certSettingEnv:                      certContainerPath,
		"GeneralSettings__PlatformEndpoint": platform,
		"PostgreSqlSettings__AdminConnectionString": "Host=" + postgresAlias + ";Port=" + postgresPort +
			";Username=platform_storage_admin;Password={0};Database=storagedb",
		"PostgreSqlSettings__ConnectionString": "Host=" + postgresAlias + ";Port=" + postgresPort +
			";Username=platform_storage;Password={0};Database=storagedb;Include Error Detail=True",
	}
	c.volumes = []types.VolumeMount{certMount(opts), namedVolume(volumeStorageBlobs, "/data/blobs")}
	c.deps = []string{containerOf(componentPostgres)}
	c.routes = []route{{host: opts.Topology.AppHostName(), prefix: "/storage"}}
	return c
}

func accessManagementComponent(opts *Options, platform string) component {
	c := newComponent(
		opts,
		componentAccessManagement,
		opts.Images.LocaltestV2.AccessManagement,
		"components/access-management",
		"5080",
		hostPortAccessManagement,
	)
	c.healthPath = healthPath
	c.env = map[string]string{
		certSettingEnv: certContainerPath,
		"Localtest__AccessManagement__PlatformEndpoint": platform,
	}
	c.volumes = []types.VolumeMount{certMount(opts), namedVolume(volumePolicyStore, "/policy-store")}
	// Its startup refresh reads the Register's streams and Storage's application list.
	c.deps = []string{containerOf(componentPostgres), containerOf(componentRegister), containerOf(componentStorage)}
	c.routes = []route{{host: opts.Topology.AppHostName(), prefix: "/accessmanagement"}}
	return c
}

func eventsComponent(opts *Options, platform string) component {
	c := newComponent(
		opts,
		componentEvents,
		opts.Images.LocaltestV2.Events,
		"components/events",
		"5080",
		hostPortEvents,
	)
	c.healthPath = healthPath
	c.env = map[string]string{
		certSettingEnv:                        certContainerPath,
		"Localtest__Events__PlatformEndpoint": platform,
		"PostgreSQLSettings__AdminConnectionString": "Host=" + postgresAlias + ";Port=" + postgresPort +
			";Username=platform_events_admin;Password={0};Database=eventsdb",
		"PostgreSQLSettings__ConnectionString": "Host=" + postgresAlias + ";Port=" + postgresPort +
			";Username=platform_events;Password={0};Database=eventsdb;Include Error Detail=True",
	}
	// Events never binds AccessTokenSettings for its outgoing platform tokens, so its production
	// SigningCredentialsResolver reads the default path relative to /app.
	eventsCert := readOnlyBind(
		filepath.Join(opts.CertDir, localtestv2.CertificateFileName),
		"/accesstoken/accesstokencredentials.pfx",
	)
	c.volumes = []types.VolumeMount{certMount(opts), eventsCert}
	c.extraHosts = append(ingressHostEntries(opts), hostDockerInternalEntry)
	c.deps = []string{
		containerOf(componentPostgres),
		containerOf(componentRegister),
		containerOf(componentAuthorization),
	}
	c.routes = []route{{host: opts.Topology.AppHostName(), prefix: "/events"}}
	return c
}

func localtestComponent(opts *Options, platform string) component {
	c := newComponent(
		opts,
		componentLocaltest,
		opts.Images.LocaltestV2.Localtest,
		"localtest",
		"8000",
		hostPortLocaltest,
	)
	c.healthPath = healthPath
	c.env = map[string]string{
		"Localtest__PlatformEndpoint":           platform,
		"Localtest__AccessTokenCertificatePath": certContainerPath,
		// Its test-user picker and token helpers call the Register and Authentication through the public
		// origin, as everything else does (the app's own proxy is not used here).
		"Proxy__Upstreams__register":       opts.Topology.LocaltestBaseURL(),
		"Proxy__Upstreams__authentication": opts.Topology.LocaltestBaseURL(),
	}
	c.volumes = []types.VolumeMount{certMount(opts), namedVolume(volumeLocaltestData, "/data")}
	// Everything the prefixes above do not claim: /, /Home, /localtest/api, /app-frontend.
	c.routes = []route{{host: opts.Topology.AppHostName(), prefix: ""}}
	return c
}

func pdfComponent(opts *Options) component {
	c := newComponent(opts, componentPDF, opts.Images.Core.PDF3, "", "5031", hostPortPDF)
	c.healthPath = "/health/ready"
	c.env = map[string]string{
		"TZ":                             "Europe/Oslo",
		"PDF3_ENVIRONMENT":               "localtest",
		"PDF3_QUEUE_SIZE":                "3",
		"PDF3_LOCALTEST_PUBLIC_BASE_URL": opts.Topology.LocaltestBaseURL(),
	}
	c.routes = []route{{host: opts.Topology.PDFHostName(), prefix: ""}}
	return c
}

func workflowEngineComponent(opts *Options) component {
	c := newComponent(
		opts,
		componentWorkflowEngine,
		opts.Images.Core.WorkflowEngine,
		"",
		"9090",
		hostPortWorkflowEngine,
	)
	c.healthPath = "/api/v1/health/ready"
	c.env = map[string]string{
		"ASPNETCORE_ENVIRONMENT": "Development",
		"ASPNETCORE_HTTP_PORTS":  "9090",
		// No collector runs in this environment; the default exporter would fail against localhost:4317.
		"EngineSettings__EnableTelemetry": "false",
		"ConnectionStrings__WorkflowEngine": "Host=" + postgresAlias + ";Port=" + postgresPort +
			";Database=workflow_engine;Username=" + postgresUser + ";Password=" + postgresPassword,
		"AppCommandSettings__CommandEndpoint": opts.Topology.LocaltestBaseURL() +
			"/{Org}/{App}/instances/{InstanceOwnerPartyId}/{InstanceGuid}/workflow-engine-callbacks/",
	}
	c.deps = []string{containerOf(componentPostgres)}
	c.routes = []route{{host: opts.Topology.WorkflowEngineHostName(), prefix: ""}}
	return c
}

// DevBuildable lists the components a dev build can produce from the research checkout.
func DevBuildable() []string {
	var names []string
	for _, c := range components(defaultOptions(config.DefaultImages(), "")) {
		if c.devContext != "" {
			names = append(names, c.name)
		}
	}
	return names
}

func defaultOptions(images config.ImagesConfig, certDir string) *Options {
	return &Options{
		DevComponents: nil,
		Images:        images,
		Topology:      envtopology.NewLocal(envtopology.DefaultIngressPortString()),
		CertDir:       certDir,
		DevSourceRoot: "",
		RuntimeUser:   "",
		UsernsMode:    "",
		RelabelBinds:  false,
	}
}

func certMount(opts *Options) types.VolumeMount {
	return readOnlyBind(opts.CertDir, certContainerDir)
}

func readOnlyBind(hostPath, containerPath string) types.VolumeMount {
	return types.VolumeMount{
		HostPath:       hostPath,
		ContainerPath:  containerPath,
		Type:           types.VolumeMountTypeBind,
		SELinuxRelabel: types.SELinuxRelabelNone,
		ReadOnly:       true,
	}
}

func namedVolume(name, containerPath string) types.VolumeMount {
	return types.VolumeMount{
		HostPath:       name,
		ContainerPath:  containerPath,
		Type:           types.VolumeMountTypeVolume,
		SELinuxRelabel: types.SELinuxRelabelNone,
		ReadOnly:       false,
	}
}

// resources turns the components into the devenv resource graph: the network, then per component an image
// (pulled, or built from the research checkout) and a container.
func resources(opts *Options) []resource.Resource {
	network := &resource.Network{
		Enabled: nil,
		Name:    networkName,
		Driver:  "bridge",
		Labels:  nil,
		Lifecycle: resource.LifecycleOptions{
			HandleDestroyError: nil,
			RetainOnDestroy:    false,
		},
	}
	comps := components(opts)
	out := make([]resource.Resource, 0, 1+2*len(comps))
	out = append(out, network)
	for _, c := range comps {
		image := componentImage(opts, c)
		out = append(out, image, componentContainer(opts, c, image, resource.Ref(network)))
	}
	return out
}

func componentImage(opts *Options, c component) resource.ImageResource {
	if opts.DevComponents[c.name] && c.devContext != "" && opts.DevSourceRoot != "" {
		buildContext := filepath.Join(opts.DevSourceRoot, c.devContext)
		dockerfile := filepath.Join(buildContext, "Dockerfile")
		if c.name == componentPostgres {
			dockerfile = filepath.Join(opts.DevSourceRoot, "postgres", "Dockerfile")
		}
		return &resource.BuiltImage{
			Enabled:     nil,
			ContextPath: filepath.ToSlash(buildContext),
			Dockerfile:  filepath.ToSlash(dockerfile),
			Build:       types.BuildOptions{CacheFrom: nil, CacheTo: nil},
			Tag:         containerPrefix + c.name + ":dev",
		}
	}
	policy := resource.PullIfNotPresent
	if c.image.Floating {
		policy = resource.PullAlwaysAllowStale
	}
	return &resource.PulledImage{Enabled: nil, Ref: c.image.Ref(), PullPolicy: policy}
}

func componentContainer(
	opts *Options,
	c component,
	image resource.ImageResource,
	network resource.ResourceRef,
) *resource.Container {
	var ports []types.PortMapping
	if c.hostPort != "" {
		ports = []types.PortMapping{{
			HostPort:      c.hostPort,
			ContainerPort: c.containerPort,
			HostIP:        loopback,
			Protocol:      "",
		}}
	}
	deps := make([]resource.ResourceRef, 0, len(c.deps))
	for _, dep := range c.deps {
		deps = append(deps, resource.RefID(resource.ContainerID(dep)))
	}
	user, usernsMode := opts.RuntimeUser, opts.UsernsMode
	if c.useDefaultUser {
		user, usernsMode = "", ""
	}
	volumes := make([]types.VolumeMount, 0, len(c.volumes))
	for _, volume := range c.volumes {
		if opts.RelabelBinds && volume.Type == types.VolumeMountTypeBind {
			volume.SELinuxRelabel = types.SELinuxRelabelShared
		}
		volumes = append(volumes, volume)
	}
	env := make([]string, 0, len(c.env))
	for key, value := range c.env {
		env = append(env, key+"="+value)
	}
	slices.Sort(env)

	return &resource.Container{
		HealthCheck: c.healthCheck,
		Name:        c.containerName(),
		Image:       resource.Ref(image),
		Enabled:     nil,
		Networks:    []resource.ResourceRef{network},
		DependsOn:   deps,
		Ports:       ports,
		Volumes:     volumes,
		Env:         env,
		Labels:      nil,
		Command:     nil,
		ExtraHosts:  c.extraHosts,
		Lifecycle: resource.ContainerLifecycleOptions{
			LifecycleOptions: resource.LifecycleOptions{
				HandleDestroyError: nil,
				RetainOnDestroy:    false,
			},
			WaitForReady: true,
		},
		NetworkAliases: c.aliases,
		RestartPolicy:  "",
		User:           user,
		UsernsMode:     usernsMode,
	}
}

// containerNames lists the containers in start order, for logs and status.
func containerNames(opts *Options) []string {
	comps := components(opts)
	names := make([]string, 0, len(comps))
	for _, c := range comps {
		names = append(names, c.containerName())
	}
	return names
}
