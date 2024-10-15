#if NET6_0_OR_GREATER && !(IOS || ANDROID)
namespace Sentry.Tests.Internals;

public class MemoryMonitorTests
{
    private class Fixture
    {
        public SentryOptions Options { get; set; } = new()
        {
            Dsn = ValidDsn,
            Debug = true,
            DiagnosticLogger = Substitute.For<IDiagnosticLogger>()
        };
        public Action<string> OnDumpCollected { get; set; } = _ => { };
        public short ThresholdPercentage { get; set; } = 5;

        public MemoryMonitor GetSut()
        {
            Options.DiagnosticLogger?.IsEnabled(Arg.Any<SentryLevel>()).Returns(true);
            return new(Options, ThresholdPercentage, OnDumpCollected);
        }
    }

    private readonly Fixture _fixture = new();

    [Fact]
    public void TryGetDumpLocation_DirectoryCreationFails_ReturnsNull()
    {
        // Arrange
        _fixture.Options.FileSystem = Substitute.For<IFileSystem>();
        _fixture.Options.FileSystem.CreateDirectory(Arg.Any<string>()).Returns(false);
        using var sut = _fixture.GetSut();

        // Act
        var result = sut.TryGetDumpLocation();

        // Assert
        result.Should().BeNull();
        _fixture.Options.FileSystem.Received().CreateDirectory(Arg.Any<string>());
        _fixture.Options.FileSystem.DidNotReceive().FileExists(Arg.Any<string>());
        _fixture.Options.ReceivedLogWarning("Failed to create a directory for memory dump ({0}).", Arg.Any<string>());
    }

    [Fact]
    public void TryGetDumpLocation_DumpFileExists_ReturnsNull()
    {
        // Arrange
        _fixture.Options.FileSystem = Substitute.For<IFileSystem>();
        _fixture.Options.FileSystem.CreateDirectory(Arg.Any<string>()).Returns(true);
        _fixture.Options.FileSystem.FileExists(Arg.Any<string>()).Returns(true);
        using var sut = _fixture.GetSut();

        // Act
        var result = sut.TryGetDumpLocation();

        // Assert
        result.Should().BeNull();
        _fixture.Options.FileSystem.Received().CreateDirectory(Arg.Any<string>());
        _fixture.Options.FileSystem.Received().FileExists(Arg.Any<string>());
        _fixture.Options.ReceivedLogWarning("Duplicate dump file detected.");
    }

    [Fact]
    public void TryGetDumpLocation_Exception_LogsError()
    {
        // Arrange
        _fixture.Options.FileSystem = Substitute.For<IFileSystem>();
        _fixture.Options.FileSystem.CreateDirectory(Arg.Any<string>()).Throws(_ => new Exception());
        using var sut = _fixture.GetSut();

        // Act
        var result = sut.TryGetDumpLocation();

        // Assert
        result.Should().BeNull();
        _fixture.Options.ReceivedLogError(Arg.Any<Exception>(), "Failed to resolve appropriate memory dump location.");
    }

    [Fact]
    public void TryGetDumpLocation_ReturnsFilePath()
    {
        // Arrange
        _fixture.Options.FileSystem = new FakeFileSystem();
        using var sut = _fixture.GetSut();

        // Act
        var result = sut.TryGetDumpLocation();

        // Assert
        result.Should().NotBeNull();
    }
}
#endif
