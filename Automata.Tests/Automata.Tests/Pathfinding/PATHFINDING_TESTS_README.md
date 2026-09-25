# Pathfinding Tests and Benchmarks

This document describes the MSTest unit tests and BenchmarkDotNet benchmarks for the ImprovedAI pathfinding system.

## Project Structure

```
ImprovedAI.Tests/
├── Pathfinding/
│   ├── PathfindingContextTests.cs      # Context creation and configuration tests
│   ├── DirectPathfinderTests.cs        # Direct pathfinding algorithm tests
│   ├── AStarPathfinderTests.cs         # A* pathfinding algorithm tests
│   └── PathfindingManagerTests.cs      # Manager coordination tests
└── TestUtil/
    ├── SEMockFactory.cs                 # Mock factory for SE API objects
    ├── FakePathfindingConfig.cs         # Test configuration implementation
    └── MockGamePruningStructureDelegate.cs

ImprovedAI.Benchmarks/
└── Pathfinding/
    ├── DirectPathfinderBenchmarks.cs           # Direct pathfinder performance
    ├── AStarPathfinderBenchmarks.cs            # A* pathfinder performance
    └── PathfindingManagerBenchmarks.cs         # Manager overhead and selection
```

## Running Unit Tests

### Prerequisites
- Visual Studio 2019+ or .NET SDK
- MSTest test adapter

### Run All Tests
```bash
# Using dotnet CLI
dotnet test ImprovedAI.Tests/ImprovedAI.Tests.csproj

# Using Visual Studio
# Open Test Explorer (Test > Test Explorer)
# Click "Run All"
```

### Run Specific Test Class
```bash
dotnet test --filter "FullyQualifiedName~DirectPathfinderTests"
dotnet test --filter "FullyQualifiedName~AStarPathfinderTests"
dotnet test --filter "FullyQualifiedName~PathfindingManagerTests"
dotnet test --filter "FullyQualifiedName~PathfindingContextTests"
```

### Run Specific Test Method
```bash
dotnet test --filter "FullyQualifiedName~DirectPathfinderTests.GetNextWaypoint_ShortDistance_ReturnsTarget"
```

## Running Benchmarks

### Prerequisites
- BenchmarkDotNet package installed
- Release build configuration (required for accurate results)

### Run All Benchmarks
```bash
# Build in Release mode first
dotnet build ImprovedAI.Benchmarks/ImprovedAI.Benchmarks.csproj -c Release

# Run benchmarks
dotnet run --project ImprovedAI.Benchmarks/ImprovedAI.Benchmarks.csproj -c Release
```

### Run Specific Benchmark Class
```bash
dotnet run --project ImprovedAI.Benchmarks/ImprovedAI.Benchmarks.csproj -c Release --filter "*DirectPathfinderBenchmarks*"
dotnet run --project ImprovedAI.Benchmarks/ImprovedAI.Benchmarks.csproj -c Release --filter "*AStarPathfinderBenchmarks*"
dotnet run --project ImprovedAI.Benchmarks/ImprovedAI.Benchmarks.csproj -c Release --filter "*PathfindingManagerBenchmarks*"
```

### Benchmark Output
Results are saved to `BenchmarkDotNet.Artifacts/results/` with:
- HTML reports
- Markdown summaries
- CSV data files
- Memory diagrams (when using MemoryDiagnoser)

## Test Coverage

### PathfindingContextTests
Tests the creation and configuration of pathfinding contexts:
- ✅ Basic context creation with controller, thrusters, sensors, cameras
- ✅ Gravity detection and planet awareness
- ✅ Thrust capability calculations
- ✅ Sensor configuration and availability
- ✅ Camera configuration by direction (all 6 controller orientations)
- ✅ Planet altitude calculation
- ✅ Safe altitude checks

### DirectPathfinderTests
Tests the direct pathfinding algorithm:
- ✅ Availability checks
- ✅ Complexity estimation (always returns 1)
- ✅ Short distance waypoint generation (returns target)
- ✅ Long distance waypoint generation (intermediate waypoints)
- ✅ Gravity-aware pathfinding with altitude maintenance
- ✅ Obstacle detection and repositioning
- ✅ Repathing enabled/disabled behavior
- ✅ Complete path generation
- ✅ Below minimum altitude handling
- ✅ Multiple reposition attempts

### AStarPathfinderTests
Tests the A* pathfinding algorithm:
- ✅ Availability checks
- ✅ Complexity estimation based on distance
- ✅ Simple direct path generation
- ✅ Obstacle avoidance pathfinding
- ✅ Node limit enforcement
- ✅ Waypoint generation (short and long distances)
- ✅ Gravity-aware pathfinding preferences
- ✅ Complex obstacle field navigation
- ✅ Fallback behavior without sensors/cameras
- ✅ Safe altitude maintenance

### PathfindingManagerTests
Tests the pathfinding manager coordination:
- ✅ Short distance pathfinder selection
- ✅ Long distance pathfinder selection
- ✅ Sensor-based pathfinder preference
- ✅ A* disabled fallback to direct
- ✅ All pathfinders disabled failure
- ✅ Failed pathfinder repathing
- ✅ Repathing disabled behavior
- ✅ Complete path generation
- ✅ Gravity-aware altitude maintenance
- ✅ Below safe altitude lifting
- ✅ Very short distance handling
- ✅ Complexity-based fallback

## Benchmark Scenarios

### DirectPathfinderBenchmarks
Performance tests for direct pathfinding:
- **Distance Variations**: 100m, 500m, 1000m, 5000m
- **Scenarios**:
  - No gravity environment
  - With gravity (planet-aware corrections)
  - With obstacle (repositioning behavior)
- **Methods Tested**:
  - `GetNextWaypoint()` - dynamic waypoint generation
  - `CalculatePath()` - complete path generation

### DirectPathfinderParameterBenchmarks
Tests impact of configuration parameters:
- **MaxRepositionAttempts**: 1, 3, 5, 10
- **WaypointDistance**: 25m, 50m, 100m, 200m
- Measures how parameters affect performance with obstacles

### AStarPathfinderBenchmarks
Performance tests for A* pathfinding:
- **Distance Variations**: 500m, 1000m, 2000m
- **Scenarios**:
  - No obstacles (baseline)
  - Single obstacle (simple avoidance)
  - Multiple obstacles (complex navigation)
  - With gravity (altitude-aware)
- **Methods Tested**:
  - `EstimatedComplexity()` - complexity calculation
  - `GetNextWaypoint()` - dynamic waypoint generation
  - `CalculatePath()` - complete path generation

### AStarParameterBenchmarks
Tests impact of A* configuration:
- **MaxPathNodes**: 100, 500, 1000, 2000
- **WaypointDistance**: 50m, 100m, 200m
- Measures how node limits and waypoint spacing affect performance

### PathfindingManagerBenchmarks
Manager coordination overhead tests:
- **Pathfinder Configurations**:
  - Direct pathfinding only
  - Direct + A* enabled
- **Scenarios**:
  - No sensors (prefers direct)
  - With sensors (may use A*)
  - With gravity
- **Distance Variations**: Short (100m), Medium (500m), Long (2000m)
- **Methods Tested**:
  - `GetNextWaypoint()` - pathfinder selection overhead
  - `GenerateCompletePath()` - full coordination overhead

### PathfindingManagerSelectionBenchmarks
Pathfinder selection logic performance:
- **Low Complexity**: Short distance, no obstacles → Direct pathfinding
- **High Complexity**: With sensors → A* consideration
- **Very Long Distance**: Complexity-based selection
- Baseline comparison between selection strategies

## Performance Expectations

### DirectPathfinder
- **GetNextWaypoint**: < 50μs (no gravity, no obstacles)
- **GetNextWaypoint with gravity**: < 100μs (altitude corrections)
- **GetNextWaypoint with repositioning**: < 500μs (3 attempts)
- **Memory**: Minimal allocations due to pre-allocated buffers

### AStarPathfinder
- **GetNextWaypoint (no obstacles)**: < 200μs
- **GetNextWaypoint (with obstacles)**: < 2ms (depends on complexity)
- **CalculatePath**: Scales with distance and obstacle count
- **Memory**: Higher than DirectPathfinder due to node pool usage

### PathfindingManager
- **Selection Overhead**: < 10μs (decision-making)
- **GetNextWaypoint**: Depends on selected pathfinder + overhead
- **Memory**: Minimal additional overhead

## Interpreting Results

### Benchmark Metrics
- **Mean**: Average execution time
- **Error**: Half of 99.9% confidence interval
- **StdDev**: Standard deviation of measurements
- **Gen0/Gen1/Gen2**: Garbage collections per 1000 operations
- **Allocated**: Memory allocated per operation

### What to Look For
1. **Regressions**: Compare against baseline/previous runs
2. **Memory Allocations**: Should be minimal (< 1KB per operation)
3. **Scaling**: Performance should scale linearly with distance
4. **Overhead**: Manager overhead should be < 10% of pathfinding time

## Continuous Integration

### Automated Test Runs
```yaml
# Example CI configuration
- name: Run Unit Tests
  run: dotnet test ImprovedAI.Tests/ImprovedAI.Tests.csproj --logger "trx"

- name: Run Benchmarks (on demand)
  if: github.event_name == 'workflow_dispatch'
  run: dotnet run --project ImprovedAI.Benchmarks/ImprovedAI.Benchmarks.csproj -c Release
```

### Test Results
- Unit tests should run on every commit
- Benchmarks should run on:
  - Release branches
  - Performance-related PRs
  - Manual triggers
  - Weekly scheduled runs

## Troubleshooting

### Tests Failing
1. Check that all SE API mocks are properly configured in SEMockFactory
2. Verify FakePathfindingConfig implements all required properties
3. Ensure MockGamePruningStructureDelegate is returning expected values
4. Check test isolation - each test should be independent

### Benchmarks Not Running
1. Ensure project is built in **Release** mode (Debug mode benchmarks are invalid)
2. Verify BenchmarkDotNet package is installed
3. Close other applications to reduce system noise
4. Disable antivirus temporarily if it's interfering

### Inconsistent Benchmark Results
1. Run benchmarks multiple times and average results
2. Close background applications
3. Disable CPU throttling/power saving
4. Use `[SimpleJob(warmupCount: 5, iterationCount: 20)]` for more iterations
5. Run on dedicated benchmark machine if possible

## Adding New Tests

### Unit Test Template
```csharp
[TestClass]
public class MyPathfinderTests
{
    private FakePathfindingConfig _config;
    private MyPathfinder _pathfinder;

    [TestInitialize]
    public void Setup()
    {
        _config = new FakePathfindingConfig
        {
            // Configure test settings
        };
        _pathfinder = new MyPathfinder(_config);
    }

    [TestMethod]
    public void MethodName_Scenario_ExpectedBehavior()
    {
        // Arrange
        var context = CreateTestContext();
        var input = new Vector3D(0, 0, 0);

        // Act
        bool result = _pathfinder.SomeMethod(ref context, ref input, out var output);

        // Assert
        Assert.IsTrue(result);
        Assert.IsNotNull(output);
    }

    private PathfindingContext CreateTestContext()
    {
        var controller = SEMockFactory.CreateMockController(new Vector3D(0, 0, 0));
        return new PathfindingContext(
            _config,
            controller,
            new List<IMySensorBlock>(),
            new List<IMyCameraBlock>(),
            CreateBasicThrusters(),
            1000f, 5000f, 50f,
            Base6Directions.Direction.Forward,
            new MockGamePruningStructureDelegate()
        );
    }

    private List<IMyThrust> CreateBasicThrusters()
    {
        return new List<IMyThrust>
        {
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Forward, 100000f),
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Backward, 100000f),
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Up, 100000f),
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Down, 100000f),
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Left, 100000f),
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Right, 100000f)
        };
    }
}
```

### Benchmark Template
```csharp
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class MyPathfinderBenchmarks
{
    private MyPathfinder _pathfinder;
    private PathfindingContext _context;
    private Vector3D _start;
    private Vector3D _end;

    [GlobalSetup]
    public void Setup()
    {
        var config = new FakePathfindingConfig
        {
            // Configure benchmark settings
        };
        _pathfinder = new MyPathfinder(config);
        _context = CreateBenchmarkContext(config);
        
        _start = new Vector3D(0, 0, 0);
        _end = new Vector3D(1000, 0, 0);
    }

    [Benchmark]
    public void MethodName_Scenario()
    {
        _pathfinder.SomeMethod(ref _context, ref _start, ref _end, out _);
    }

    private PathfindingContext CreateBenchmarkContext(IPathfindingConfig config)
    {
        var controller = SEMockFactory.CreateMockController(new Vector3D(0, 0, 0));
        var thrusters = CreateBasicThrusters();

        return new PathfindingContext(
            config,
            controller,
            new List<IMySensorBlock>(),
            new List<IMyCameraBlock>(),
            thrusters,
            1000f, 5000f, 50f,
            Base6Directions.Direction.Forward,
            new MockGamePruningStructureDelegate()
        );
    }

    private List<IMyThrust> CreateBasicThrusters()
    {
        return new List<IMyThrust>
        {
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Forward, 100000f),
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Backward, 100000f),
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Up, 100000f),
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Down, 100000f),
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Left, 100000f),
            SEMockFactory.CreateMockThruster(Base6Directions.Direction.Right, 100000f)
        };
    }
}
```

## Best Practices

### Unit Tests
1. **Arrange-Act-Assert**: Use clear AAA pattern
2. **One assertion per test**: Test one behavior per method
3. **Descriptive names**: Use `MethodName_Scenario_ExpectedBehavior` format
4. **Independent tests**: Each test should be runnable in isolation
5. **Use test helpers**: Extract common setup into helper methods
6. **Test edge cases**: Test boundary conditions, null inputs, etc.

### Benchmarks
1. **Release mode only**: Never benchmark Debug builds
2. **Warm up properly**: Use sufficient warmup iterations
3. **Multiple iterations**: Run enough iterations for statistical significance
4. **Minimize noise**: Close other applications
5. **Baseline comparisons**: Mark one benchmark as baseline for relative comparison
6. **Memory profiling**: Use MemoryDiagnoser to track allocations
7. **Parameter variations**: Use `[Params]` to test different configurations

## Performance Optimization Guidelines

Based on benchmark results, consider these optimizations:

### If GetNextWaypoint is slow:
1. Check for unnecessary allocations (use MemoryDiagnoser)
2. Verify ref parameters are used correctly
3. Look for redundant calculations
4. Consider caching frequently used values

### If CalculatePath is slow:
1. Review waypoint spacing (larger spacing = fewer waypoints)
2. Check maxPathNodes limit (lower = faster but less accurate)
3. Optimize obstacle checking logic
4. Consider using direct pathfinding for simple cases

### If repositioning is slow:
1. Reduce maxRepositionAttempts
2. Optimize perpendicular direction calculations
3. Cache sensor positions
4. Use spatial hashing for obstacle checks

### Memory Optimization:
1. Pre-allocate buffers and reuse them
2. Use ref parameters to avoid copying
3. Pool large objects
4. Avoid LINQ in hot paths
5. Use struct instead of class where appropriate (with care)

## Integration with CI/CD

### GitHub Actions Example
```yaml
name: Tests and Benchmarks

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main, develop ]
  workflow_dispatch:
    inputs:
      run_benchmarks:
        description: 'Run benchmarks'
        required: false
        type: boolean
        default: false

jobs:
  test:
    runs-on: windows-latest
    steps:
    - uses: actions/checkout@v3
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: '6.0.x'
    
    - name: Restore dependencies
      run: dotnet restore
    
    - name: Build
      run: dotnet build --no-restore
    
    - name: Run Unit Tests
      run: dotnet test ImprovedAI.Tests/ImprovedAI.Tests.csproj --no-build --verbosity normal --logger "trx;LogFileName=test-results.trx"
    
    - name: Publish Test Results
      uses: dorny/test-reporter@v1
      if: always()
      with:
        name: Test Results
        path: '**/test-results.trx'
        reporter: dotnet-trx

  benchmark:
    runs-on: windows-latest
    if: github.event_name == 'workflow_dispatch' && github.event.inputs.run_benchmarks == 'true'
    steps:
    - uses: actions/checkout@v3
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: '6.0.x'
    
    - name: Run Benchmarks
      run: |
        dotnet build ImprovedAI.Benchmarks/ImprovedAI.Benchmarks.csproj -c Release
        dotnet run --project ImprovedAI.Benchmarks/ImprovedAI.Benchmarks.csproj -c Release
    
    - name: Upload Benchmark Results
      uses: actions/upload-artifact@v3
      with:
        name: benchmark-results
        path: BenchmarkDotNet.Artifacts/results/
```

## Test Data and Scenarios

### Common Test Scenarios

#### Space Environment (No Gravity)
```csharp
var controller = SEMockFactory.CreateMockController(new Vector3D(0, 0, 0));
// No gravity vector, no planet
```

#### Planet Surface (Strong Gravity)
```csharp
var gravity = new Vector3(0, -9.81f, 0);
var controller = SEMockFactory.CreateMockController(new Vector3D(0, 100, 0), gravity);
// planetCenter: new Vector3D(0, -60000, 0)
// planetRadius: 60000.0
```

#### Asteroid Field (Weak Gravity, Many Obstacles)
```csharp
var gravity = new Vector3(0, -1.5f, 0);
var controller = SEMockFactory.CreateMockController(position, gravity);
// Add multiple sensor infos for obstacles
```

#### Low Altitude (Below Safe Altitude)
```csharp
var gravity = new Vector3(0, -9.81f, 0);
var controller = SEMockFactory.CreateMockController(new Vector3D(0, 30, 0), gravity);
// Altitude: 30m < minAltitudeBuffer (50m)
```

## Maintenance

### Regular Tasks
1. **Weekly**: Review test failures in CI
2. **Monthly**: Run full benchmark suite and track trends
3. **Per Release**: Run benchmarks and update performance baselines
4. **After Major Changes**: Add new tests for new functionality

### Documentation
- Keep this README updated with new test classes
- Document any special test setup requirements
- Update benchmark expectations after optimizations
- Add examples for complex test scenarios

## References

- [MSTest Documentation](https://docs.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-mstest)
- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [Space Engineers ModAPI Documentation](https://keensoftwarehouse.github.io/SpaceEngineersModAPI/)
- [ImprovedAI Project Documentation](../README.md)

## Contact

For questions about tests or benchmarks:
- Open an issue on GitHub
- Check existing test examples in the codebase
- Review the ModScript examples wiki