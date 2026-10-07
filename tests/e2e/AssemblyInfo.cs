using Xunit;

// The Docker-based end-user E2E classes (EndUserE2ETests, DockerEndUserSmokeE2ETests,
// CrossDatabaseEndUserE2ETests) each run `docker compose up --build` against the same
// Dockerfile. xUnit runs test classes in parallel by default, and concurrent builds
// race on the shared build cache and Docker daemon state; one test's
// `down --remove-orphans` can also reap another test's containers. Serializing test
// collections keeps the CLI-only tests fast (they are spread across many collections)
// while making the Docker suites safe.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly, DisableTestParallelization = true)]
