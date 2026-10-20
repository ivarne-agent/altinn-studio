package localtestv2

import (
	"slices"
	"testing"

	"altinn.studio/devenv/pkg/resource"
	"altinn.studio/studioctl/internal/config"
	"altinn.studio/studioctl/internal/envtopology"
)

func TestBoundTopologyConfigRoutesEveryComponentToLoopback(t *testing.T) {
	t.Parallel()
	opts := defaultOptions(config.DefaultImages(), t.TempDir())
	cfg := boundTopologyConfig(opts)

	if cfg.AppRouteTemplate.Host != "local.altinn.cloud" || cfg.AppRouteTemplate.PathPrefixTemplate != "/{org}/{app}" {
		t.Fatalf("app route template = %+v", cfg.AppRouteTemplate)
	}
	byPrefix := map[string]string{}
	for _, route := range cfg.Routes {
		if route.Destination.Location != envtopology.DestinationLocationHost {
			t.Errorf("%s: location %q, want host", route.Component, route.Destination.Location)
		}
		byPrefix[route.Match.Host+route.Match.PathPrefix] = route.Destination.URL
	}
	want := map[string]string{
		"local.altinn.cloud/storage":          "http://127.0.0.1:" + hostPortStorage,
		"local.altinn.cloud/profile":          "http://127.0.0.1:" + hostPortRegister,
		"local.altinn.cloud":                  "http://127.0.0.1:" + hostPortLocaltest,
		"pdf.local.altinn.cloud":              "http://127.0.0.1:" + hostPortPDF,
		"workflow-engine.local.altinn.cloud":  "http://127.0.0.1:" + hostPortWorkflowEngine,
		"app-frontend.local.altinn.cloud":     "http://127.0.0.1:" + frontendDevServerHostPort,
		"local.altinn.cloud/accessmanagement": "http://127.0.0.1:" + hostPortAccessManagement,
		"local.altinn.cloud/events":           "http://127.0.0.1:" + hostPortEvents,
		"local.altinn.cloud/authentication":   "http://127.0.0.1:" + hostPortAuthentication,
		"local.altinn.cloud/authorization":    "http://127.0.0.1:" + hostPortAuthorization,
		"local.altinn.cloud/register":         "http://127.0.0.1:" + hostPortRegister,
	}
	for key, url := range want {
		if byPrefix[key] != url {
			t.Errorf("route %s = %q, want %q", key, byPrefix[key], url)
		}
	}
}

func TestComponentsReachThePlatformThroughTheHostGateway(t *testing.T) {
	t.Parallel()
	for _, c := range components(defaultOptions(config.DefaultImages(), t.TempDir())) {
		if c.name == componentPostgres {
			continue
		}
		for _, host := range []string{"local.altinn.cloud", "app-frontend.local.altinn.cloud", "pdf.local.altinn.cloud"} {
			if !slices.Contains(c.extraHosts, host+":host-gateway") {
				t.Errorf("%s: extra hosts %v lack %s", c.name, c.extraHosts, host)
			}
		}
		if c.hostPort == "" || c.healthPath == "" {
			t.Errorf("%s: needs a host port and a health path for the health wait", c.name)
		}
	}
}

func TestDevComponents(t *testing.T) {
	t.Parallel()
	all, err := devComponents([]string{"all"})
	if err != nil {
		t.Fatal(err)
	}
	if !all[componentStorage] || all[componentPDF] {
		t.Errorf("all = %v: storage is buildable, pdf3 is not", all)
	}
	if _, err := devComponents([]string{"pdf3"}); err == nil {
		t.Error("pdf3 comes from the monorepo and must be rejected")
	}
	if _, err := devComponents([]string{"nosuch"}); err == nil {
		t.Error("unknown component must be rejected")
	}
}

func TestDevComponentImageIsBuiltFromTheResearchCheckout(t *testing.T) {
	t.Parallel()
	opts := defaultOptions(config.DefaultImages(), t.TempDir())
	opts.DevSourceRoot = "/src/localtest"
	opts.DevComponents = map[string]bool{componentStorage: true, componentPostgres: true}
	for _, c := range components(opts) {
		img := componentImage(opts, c)
		built, isBuilt := img.(*resource.BuiltImage)
		switch c.name {
		case componentStorage:
			if !isBuilt || built.Dockerfile != "/src/localtest/components/storage/Dockerfile" {
				t.Errorf("storage image = %#v", img)
			}
		case componentPostgres:
			if !isBuilt || built.Dockerfile != "/src/localtest/postgres/Dockerfile" ||
				built.ContextPath != "/src/localtest" {
				t.Errorf("postgres image = %#v", img)
			}
		default:
			if isBuilt {
				t.Errorf("%s: built although not selected", c.name)
			}
		}
	}
}
