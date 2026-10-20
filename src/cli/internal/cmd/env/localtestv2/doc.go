// Package localtestv2 runs the localtest-v2 environment: the production platform components (Register,
// Authentication, the PDP, Access Management, Storage, Events) built by the localtest-research repository,
// the localtest app that hosts the fake ID-porten, Maskinporten and the /Home test helpers, a shared
// Postgres, and the PDF generator and workflow engine the localtest runtime also runs.
//
// studioctl-server is the front door: it serves the platform's public origin (http://local.altinn.cloud:8000)
// on the host and forwards each path prefix to the component's loopback-published port, and /{org}/{app} to
// the app it discovered, so apps keep binding to loopback and there is no host bridge. The components reach
// the public origin through the container runtime's host gateway.
package localtestv2
