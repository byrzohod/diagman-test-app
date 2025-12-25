# DiagMan Test Application

A .NET 10 application with intentional bugs designed to test DiagMan's diagnostic capabilities. Each scenario produces specific failure patterns that DiagMan should detect and diagnose using:
- Kubernetes pod/container status
- Container logs
- Prometheus metrics (CPU, memory, restarts)
- GitHub RAG (source code analysis)

## Test Scenarios

### Scenario 1: Memory Leak (OOM Kill)
**File:** `Services/CacheService.cs`
**Bug:** Cache entries are never evicted, causing unbounded memory growth.
**Symptom:** Pod gets OOMKilled repeatedly, high memory usage in Prometheus.
**What DiagMan should find:** The `_cache` dictionary grows unbounded - no eviction policy.

### Scenario 2: Configuration Crash (CrashLoopBackOff)
**File:** `Services/ConfigurationService.cs`
**Bug:** `PAYMENT_API_SECRET` environment variable is used without validation.
**Symptom:** CrashLoopBackOff with NullReferenceException on startup.
**What DiagMan should find:** Missing env var validation for `PAYMENT_API_SECRET`.

### Scenario 3: Database Connection Exhaustion
**File:** `Services/DatabaseService.cs`
**Bug:** Database connections are not disposed in exception paths.
**Symptom:** Timeouts waiting for connections, app becomes unresponsive.
**What DiagMan should find:** Connection not closed in catch block of `GetUserWithRetryAsync`.

### Scenario 4: Deadlock
**File:** `Services/OrderProcessingService.cs`
**Bug:** Lock ordering inconsistency (ABBA deadlock pattern).
**Symptom:** Requests hang indefinitely, high CPU with no progress.
**What DiagMan should find:** `_inventoryLock` and `_orderLock` acquired in opposite orders.

### Scenario 5: External API Timeout Cascade
**File:** `Services/ExternalApiService.cs`
**Bug:** No circuit breaker, timeout per-request instead of per-operation.
**Symptom:** Cascading failures, readiness probe failures, pod restarts.
**What DiagMan should find:** Missing circuit breaker pattern, wrong timeout placement.

## Building

```bash
# Build locally
cd src/DiagManTestApp
dotnet build

# Build Docker image
docker build -t diagman-test-app:latest .

# For kind cluster
kind load docker-image diagman-test-app:latest
```

## Deploying to Kubernetes

```bash
# Create namespace
kubectl apply -f k8s/namespace.yaml

# Deploy specific scenario
kubectl apply -f k8s/scenario-1-memory-leak.yaml
# or
kubectl apply -f k8s/scenario-2-config-crash.yaml
# etc.

# Watch pod status
kubectl get pods -n diagman-test -w
```

## Triggering Issues via API

For scenarios that don't auto-trigger, use the API endpoints:

```bash
# Memory leak (manual trigger instead of background service)
curl -X POST http://localhost:8080/api/diagnostics/memory-leak?iterations=20

# Deadlock
curl -X POST http://localhost:8080/api/diagnostics/deadlock

# Timeout cascade
curl -X POST http://localhost:8080/api/diagnostics/timeout-cascade?resourceCount=50

# Get current stats
curl http://localhost:8080/api/diagnostics/stats
```

## Configuring DiagMan to Use This Repo

1. **Register the GitHub repository with DiagMan:**
```bash
# Create repository mapping
curl -X POST http://localhost:5050/api/repositories \
  -H "Content-Type: application/json" \
  -d '{
    "fullName": "your-org/diagman-test-app",
    "defaultBranch": "main"
  }'
```

2. **Create service-to-repo mapping:**
```bash
curl -X POST http://localhost:5050/api/service-mappings \
  -H "Content-Type: application/json" \
  -d '{
    "serviceNamespace": "diagman-test",
    "serviceName": "testapp-memory-leak",
    "repositoryFullName": "your-org/diagman-test-app",
    "repositoryPath": "src/DiagManTestApp"
  }'
```

3. **Index the repository for RAG:**
```bash
curl -X POST http://localhost:5050/api/repositories/{repoId}/index
```

## Expected DiagMan Analysis

When an incident occurs, DiagMan should:

1. **Detect the problem** via pod watcher (CrashLoopBackOff, OOMKilled, etc.)
2. **Collect context:**
   - Container logs showing errors/warnings
   - Pod events
   - Container status
   - Prometheus metrics (if enabled)
3. **Fetch source code** using GitHub RAG based on service mapping
4. **Analyze with AI** to identify the root cause in the source code
5. **Provide fix suggestions** pointing to the specific buggy code

## Project Structure

```
diagman-test-app/
├── src/DiagManTestApp/
│   ├── Services/
│   │   ├── CacheService.cs           # Memory leak bug
│   │   ├── DatabaseService.cs        # Connection leak bug
│   │   ├── ConfigurationService.cs   # Missing env var bug
│   │   ├── OrderProcessingService.cs # Deadlock bug
│   │   └── ExternalApiService.cs     # Timeout cascade bug
│   ├── Controllers/
│   │   └── DiagnosticsController.cs  # API to trigger issues
│   ├── BackgroundServices/
│   │   └── MemoryLeakBackgroundService.cs
│   └── Program.cs
├── k8s/
│   ├── namespace.yaml
│   ├── scenario-1-memory-leak.yaml
│   ├── scenario-2-config-crash.yaml
│   ├── scenario-3-db-exhaustion.yaml
│   ├── scenario-4-deadlock.yaml
│   └── scenario-5-timeout-cascade.yaml
└── Dockerfile
```
