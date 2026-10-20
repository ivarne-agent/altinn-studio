package localtestv2

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"time"

	"altinn.studio/studioctl/internal/ui"
)

const (
	healthTimeout      = 3 * time.Minute
	healthPollInterval = time.Second
	healthRequestLimit = 5 * time.Second
)

var errUnhealthy = errors.New("not healthy")

// healthTarget is one URL that must answer 200 before the environment counts as up.
type healthTarget struct {
	name string
	url  string
}

// healthTargets are the components' own health endpoints on their host ports, then the public origin
// through studioctl-server, which proves the proxy forwards.
func healthTargets(opts *Options) []healthTarget {
	var targets []healthTarget
	for _, c := range components(opts) {
		if c.healthPath == "" || c.hostPort == "" {
			continue
		}
		targets = append(targets, healthTarget{name: c.name, url: c.hostURL() + c.healthPath})
	}
	targets = append(targets, healthTarget{
		name: "proxy",
		url:  opts.Topology.LocaltestURL() + "/Home/Localtest/Version",
	})
	return targets
}

// waitHealthy polls every target over HTTP until each has answered 200 once, or the timeout passes.
func waitHealthy(ctx context.Context, out *ui.Output, targets []healthTarget) error {
	ctx, cancel := context.WithTimeout(ctx, healthTimeout)
	defer cancel()
	client := &http.Client{Timeout: healthRequestLimit}
	pending := append([]healthTarget(nil), targets...)
	lastErr := map[string]string{}
	for len(pending) > 0 {
		remaining := pending[:0]
		for _, target := range pending {
			if err := probe(ctx, client, target.url); err != nil {
				lastErr[target.name] = err.Error()
				remaining = append(remaining, target)
				continue
			}
			out.Verbosef("%s healthy at %s", target.name, target.url)
		}
		pending = remaining
		if len(pending) == 0 {
			return nil
		}
		select {
		case <-ctx.Done():
			names := make([]string, 0, len(pending))
			for _, target := range pending {
				names = append(names, target.name+" ("+target.url+": "+lastErr[target.name]+")")
			}
			return fmt.Errorf("%w after %s: %s", errUnhealthy, healthTimeout, strings.Join(names, ", "))
		case <-time.After(healthPollInterval):
		}
	}
	return nil
}

func probe(ctx context.Context, client *http.Client, url string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return fmt.Errorf("build request: %w", err)
	}
	resp, err := client.Do(req)
	if err != nil {
		return fmt.Errorf("request: %w", err)
	}
	defer closeBody(resp)
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("%w: HTTP %d", errUnhealthy, resp.StatusCode)
	}
	return nil
}

func closeBody(resp *http.Response) {
	ignoreError(resp.Body.Close())
}

func ignoreError(error) {}
