using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Celeste.Core.IO.Content;
using Celeste.Everest.Runtime.Compatibility;
using Celeste.Game.Profiles;
using Celeste.Game.Runtime;
using Celeste.Game.Runtime.Physical.Files;
using Celeste.Game.Runtime.Physical.Memory;
using Celeste.Game.Runtime.Physical.Processes;
using Celeste.Game.Runtime.Physical.Runtime;
using Celeste.Game.Runtime.Platform;
using Celeste.Godot.Runtime.Diagnostics;
using Celeste.Godot.Runtime.Graphics;
using Celeste.Godot.Runtime.Input;
using Celeste.Godot.Runtime.Interop;
using Celeste.Godot.Runtime.Networking;
using Celeste.Godot.Runtime.Startup;
using Celeste.Godot.Runtime.Windowing;
using Celeste.Graphics.Rendering.Devices;
using Celeste.Hosting.Runtime.Content;
using Celeste.Platform.Native.Audio.Capture;
using Celeste.Platform.Native.Audio.FAudio;
using Celeste.Platform.Native.Console;
using Celeste.Platform.Native.Displays;
using Celeste.Platform.Native.Images.Fna;
using Celeste.Platform.Native.Media.Theora;
using Celeste.Platform.Native.Randomness;
using Celeste.Platform.Native.Sdl;
using Celeste.Platform.Native.Timing;
using Celeste.Platform.Native.Windowing;
using Godot;
using Environment = System.Environment;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime
{
    public sealed partial class GodotBootstrap : Node
    {
        [ExportGroup("Launch")] [Export] private GameProfile sourceProfile = GameProfile.Original;

        [Export] private GodotGraphicsBackend graphicsBackend = GodotGraphicsBackend.Auto;
        private GameApplication? _process;
        private GodotSourceInputDevices? _inputDevices;
        private GodotSourceInputBridge? _inputBridge;
        private NativeSourceDisplayIO? _displays;
        private GodotSourceWindowIO? _physicalWindow;
        private Win32WindowStartupVisibility? _startupWindowVisibility;
        private GodotWindowAppearance? _windowAppearance;
        private bool _previousRenderLoopEnabled;
        private bool _renderLoopOwned;
        private IGodotGraphicsHost? _graphicsSubmission;
        private SocketsCompressedHttpTransport? _compressedHttp;
        private GodotNativeLibraryResolver? _nativeLibraries;
        private bool _clientSizeChangedBound;
        private bool _clientSizeChangePending;
        private bool _started;
        private bool _disposed;

        public override void _Ready()
        {
            try
            {
                InitializeSourceHost();
            }
            catch (Exception exception)
            {
                ReportSourceException(exception);
                AbortSourceRun();
                throw;
            }
        }

        private void InitializeSourceHost()
        {
            _previousRenderLoopEnabled = RenderingServer.RenderLoopEnabled;
            RenderingServer.RenderLoopEnabled = false;
            _renderLoopOwned = true;
            ProcessMode = ProcessModeEnum.Always;
            GetTree().AutoAcceptQuit = false;
            var window = GetWindow();
            if (OperatingSystem.IsWindows() &&
                !string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase))
            {
                _startupWindowVisibility = new(new(
                    DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, window.GetWindowId())));
                var nativeIcon = ProjectSettings.GetSetting("application/config/windows_native_icon", string.Empty)
                    .AsString();
                if (nativeIcon.Length != 0) DisplayServer.SetNativeIcon(nativeIcon);
                _windowAppearance = new(new(
                    DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, window.GetWindowId())));
            }

            window.Borderless = false;
            window.Unfocusable = false;
            window.Transparent = false;
            GetViewport().TransparentBg = false;
            window.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
            string[] userArguments = OS.GetCmdlineUserArgs();
            var profile = GameProfileSelection.Resolve(sourceProfile, userArguments);
            var editorHost = OS.HasFeature("editor");
            var applicationDirectory = editorHost
                ? Path.GetFullPath(ProjectSettings.GlobalizePath("res://"))
                : Path.GetDirectoryName(OS.GetExecutablePath())!;
            var contentPaths = new GameContentPaths(editorHost
                ? Path.Combine(applicationDirectory, "..", "Content")
                : applicationDirectory);
            var contentRoot = contentPaths.Game;
            var frameworkTimeResources = contentPaths.FrameworkTimeResources;
            GD.Print("Celeste startup: profile=" + profile + ", contentRoot=" + contentRoot +
                     ", applicationDirectory=" + applicationDirectory);
            var physicalOwnerAssemblyPath = GodotAssemblyPathIO.GetPhysicalPath(typeof(GameApplication).Assembly);
            if (!Directory.Exists(contentRoot))
                throw new DirectoryNotFoundException("The deployed game content is missing: " + contentRoot);

            _nativeLibraries = new(Path.Combine(applicationDirectory, "Native",
                GodotNativeLibraryResolver.RuntimeIdentifier));
            _nativeLibraries.Bind();
            _displays = new();
            _physicalWindow = new(window, _displays);
            _inputDevices = new(window, _physicalWindow);
            var selectedBackend =
                GodotGraphicsBackendSelection.Resolve(graphicsBackend, userArguments);
            // ReSharper disable once SwitchExpressionHandlesSomeKnownEnumValuesWithExceptionInDefault
            _graphicsSubmission = selectedBackend switch
            {
                GodotGraphicsBackend.Direct3D11 => new GodotNativeGraphicsSubmission(_physicalWindow,
                    Environment.CurrentManagedThreadId),
                GodotGraphicsBackend.Direct3D12 => new GodotDirect3D12GraphicsHost(_physicalWindow,
                    _physicalWindow.Handle),
                GodotGraphicsBackend.Vulkan => new GodotVulkanGraphicsHost(_physicalWindow, window),
                _ => throw new ArgumentOutOfRangeException(nameof(graphicsBackend)),
            };
            GD.Print("Celeste graphics: ", selectedBackend);
            var graphicsFactory = _graphicsSubmission.DeviceFactory;
            var sdl = new NativeSourceSDLRuntimeDevice();
            var runtime = new GodotSourceRuntimeIO(_physicalWindow, physicalOwnerAssemblyPath,
                applicationDirectory, [OS.GetExecutablePath(), .. userArguments],
                typeof(GodotBootstrap).GetMethod(nameof(_Ready),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null) ??
                throw new MissingMethodException(typeof(GodotBootstrap).FullName, nameof(_Ready)),
                DisplayServer.IsTouchscreenAvailable, sdl.SetHint);
            var clock = new SourceStopwatchGameClockDevice();
            if (profile == GameProfile.InstalledEverest) _compressedHttp = new();

            var physical = new GamePlatformServices(physicalOwnerAssemblyPath, contentRoot,
                _physicalWindow, graphicsFactory, _displays,
                new ProjectedContentSource(new DirectoryContentSource(contentRoot),
                    new Dictionary<string, string> { { ".meta", ".atlas.bytes" } },
                    [".meta"]),
                new NativeSourceFnaImageCodecDevice(), _inputDevices.Keyboard, _inputDevices.MouseTouch,
                _inputDevices.GamePads,
                clock, clock, new NativeSourceRandomSeeds(), new NativeSourceFAudioIOFactory(),
                new NativeSourceMicrophoneIO(), new NativeSourceTheoraIO(), runtime, _inputDevices,
                new NativeSourceFrameworkTimeResources(frameworkTimeResources),
                runtime, runtime, sdl, runtime, runtime, null, runtime, runtime,
                profile == GameProfile.InstalledEverest ? new ProcessSourceEverestSplashIO() : null,
                new NativeSourceConsoleModeIO(), _graphicsSubmission.DrawTransport, runtime, _inputDevices,
                new ManagedSourceRuntimeDiagnostics(new NativeSourceRuntimeMemoryCapacity()),
                null, _inputDevices, _compressedHttp,
                assemblyMetadataRuntimeFactory: profile == GameProfile.InstalledEverest
                    ? new SourcePhysicalAssemblyContextMetadataRuntimeFactory(
                        assembly => AssemblyLoadContext.GetLoadContext(assembly) ??
                                    throw new InvalidOperationException(
                                        "An actual CLR assembly load context is required."),
                        context => ((AssemblyLoadContext)context).Assemblies,
                        typeof(GameApplication).Assembly, typeof(GodotBootstrap).Assembly)
                    : null,
                audioOutputAvailable: !string.Equals(DisplayServer.GetName(), "headless",
                                          StringComparison.OrdinalIgnoreCase) &&
                                      !string.Equals(AudioServer.GetDriverName(), "Dummy",
                                          StringComparison.OrdinalIgnoreCase),
                consoleOutput: new GodotConsoleLogWriter(),
                temporaryDirectoryPath: ProjectSettings.GlobalizePath("user://cache"));
            _process = GameApplicationFactory.Create(profile, physical);
            if (_process == null)
            {
                GetTree().Quit();
                return;
            }

            if (profile == GameProfile.InstalledEverest)
                _process.BindSourceInitializationSaveLinkIO(new SourceDirectoryLinkTargetIO());

            _process.AudioSession.Device.AudioOutputCreated += noSound => GD.Print("FMOD NoSound=", noSound);
            _graphicsSubmission.FramePresented += ShowSourceWindow;
            _inputBridge = new(_inputDevices, _process);
            clock.BindPollEvents(PollSourceEvents);
            _process.ActualGame.IsActive = window.HasFocus();
            window.SizeChanged += NotifyClientSizeChanged;
            _clientSizeChangedBound = true;
            CallDeferred(nameof(StartSourceRun));
        }

        private void ShowSourceWindow()
        {
            _graphicsSubmission!.FramePresented -= ShowSourceWindow;
            _startupWindowVisibility?.Complete();
            _process!.ActualGame.IsActive = GetWindow().HasFocus();
            // Only D3D11 replays here; shared window backends own their acquisition/presentation sequence.
            _graphicsSubmission.WindowShown();
        }

        private void StartSourceRun()
        {
            if (_disposed || _process == null) return;

            try
            {
                _process.ActualGame.IsActive = GetWindow().HasFocus();
                _process.ActualGame.StartSourceHostRun();
                _started = true;
                _graphicsSubmission!.Pump();
            }
            catch (Exception exception)
            {
                ReportSourceException(exception);
                AbortSourceRun();
                throw;
            }
        }

        public override void _Process(double delta)
        {
            if (_disposed || _process == null || !_started) return;

            try
            {
                _graphicsSubmission!.Pump();
                if (_process.ActualGame.SourceHostRunOpen) _process.ActualGame.ContinueSourceHostRun();

                _graphicsSubmission.Pump();
                if (!_process.ActualGame.SourceHostRunOpen) GetTree().Quit();
            }
            catch (Exception exception)
            {
                ReportSourceException(exception);
                AbortSourceRun();
                throw;
            }
        }

        private void ReportSourceException(Exception exception)
        {
            // One complete exception chain, rather than ToString plus duplicate StackTrace output.
            try
            {
                _process?.WriteSourceExceptionToLog(exception);
            }
            finally
            {
                GD.PushError(exception.ToString());
            }
        }

        private void AbortSourceRun()
        {
            _graphicsSubmission?.AbortUnsubmittedDraws();
            _started = false;
            SetProcess(false);
            _process?.ActualGame.GameLoop.Exit();
            GetTree().Quit(1);
        }

        public override void _Input(InputEvent input)
        {
            if (!_disposed) _inputBridge?.HandleEvent(input);
        }

        public override void _Notification(int what)
        {
            if (_disposed || _process == null) return;

            if (what == NotificationWMCloseRequest)
                _process.ActualGame.GameLoop.Exit();
            else if (what == NotificationWMWindowFocusIn || what == NotificationWMWindowFocusOut)
                _process.ActualGame.IsActive = what == NotificationWMWindowFocusIn;
        }

        private void NotifyClientSizeChanged()
        {
            // Godot emits SizeChanged synchronously from programmatic window resizing.
            // Deliver the physical event at the source event-poll boundary, after
            // GraphicsDeviceManager has completed its create/reset transaction.
            if (!_disposed) _clientSizeChangePending = true;
        }

        private void PollSourceEvents()
        {
            if (_clientSizeChangePending && _process!.ActualGame.Window is SourceHostGameWindow window)
            {
                _clientSizeChangePending = false;
                window.NotifyClientSizeChanged();
            }

            _inputBridge!.PollEvents();
        }

        private void PumpSourceShutdown()
        {
            _graphicsSubmission?.Pump();
            if (_process!.SourceProfile == GameProfile.InstalledEverest) _process.MainThreadProcess.Update(null);

            _graphicsSubmission?.Pump();
        }

        private void ReleaseSourceHostResources(bool releaseNativeLibraries)
        {
            try
            {
                _startupWindowVisibility?.Dispose();
                _startupWindowVisibility = null;
                _windowAppearance?.Dispose();
                _windowAppearance = null;
                _compressedHttp?.Dispose();
                _inputBridge?.Dispose();
                _inputDevices?.Dispose();
                _displays?.Dispose();
            }
            finally
            {
                try
                {
                    if (_graphicsSubmission != null)
                    {
                        _graphicsSubmission.FramePresented -= ShowSourceWindow;
                        _graphicsSubmission.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        if (releaseNativeLibraries) _nativeLibraries?.Dispose();
                    }
                    finally
                    {
                        if (_renderLoopOwned)
                        {
                            RenderingServer.RenderLoopEnabled = _previousRenderLoopEnabled;
                            _renderLoopOwned = false;
                        }
                    }
                }
            }
        }

        public override void _ExitTree()
        {
            if (_disposed) return;

            _disposed = true;
            if (_clientSizeChangedBound)
            {
                GetWindow().SizeChanged -= NotifyClientSizeChanged;
                _clientSizeChangedBound = false;
            }

            var threadsCompleted = _process == null;
            var sourceGameDisposed = _process == null;
            try
            {
                if (_process != null)
                {
                    _process.ActualGame.GameLoop.Exit();
                    try
                    {
                        _process.WaitSourceBackgroundThreads(PumpSourceShutdown);
                        _process.ActualGame.FinishSourceHostRun();
                    }
                    finally
                    {
                        _process.WaitSourceBackgroundThreads(PumpSourceShutdown);
                        threadsCompleted = true;
                        try
                        {
                            _graphicsSubmission?.StopPresenting();
                        }
                        finally
                        {
                            _process.ActualGame.Dispose();
                            sourceGameDisposed = true;
                        }
                    }
                }
                else
                {
                    _graphicsSubmission?.StopPresenting();
                }
            }
            finally
            {
                if (threadsCompleted) ReleaseSourceHostResources(sourceGameDisposed);
            }
        }
    }
}
