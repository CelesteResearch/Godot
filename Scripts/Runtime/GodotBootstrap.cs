using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Celeste.Core.IO.Content;
using Celeste.Game.Profiles;
using Celeste.Everest.Runtime.Compatibility;
using Celeste.Game.Runtime;
using Celeste.Game.Runtime.Physical.Files;
using Celeste.Game.Runtime.Physical.Memory;
using Celeste.Godot.Runtime.Networking;
using Celeste.Game.Runtime.Physical.Processes;
using Celeste.Game.Runtime.Platform;
using Celeste.Godot.Runtime.Graphics;
using Celeste.Godot.Runtime.Diagnostics;
using Celeste.Godot.Runtime.Input;
using Celeste.Godot.Runtime.Interop;
using Celeste.Godot.Runtime.Startup;
using Celeste.Godot.Runtime.Windowing;
using Celeste.Graphics.Rendering.Devices;
using Celeste.Platform.Native.Audio.Capture;
using Celeste.Platform.Native.Audio.FAudio;
using Celeste.Platform.Native.Console;
using Celeste.Game.Runtime.Physical.Runtime;
using Celeste.Platform.Native.Displays;
using Celeste.Platform.Native.Images.Fna;
using Celeste.Platform.Native.Media.Theora;
using Celeste.Platform.Native.Randomness;
using Celeste.Platform.Native.Sdl;
using Celeste.Platform.Native.Timing;
using Celeste.Platform.Native.Windowing;
using global::Godot;

namespace Celeste.Godot.Runtime
{
    public sealed partial class GodotBootstrap : Node
    {
        [ExportGroup("Launch")] [Export] private GameProfile sourceProfile = GameProfile.Original;
        [Export] private GodotGraphicsBackend graphicsBackend = GodotGraphicsBackend.Auto;
        private GameApplication? process;
        private GodotSourceInputDevices? inputDevices;
        private GodotSourceInputBridge? inputBridge;
        private NativeSourceDisplayIO? displays;
        private GodotSourceWindowIO? physicalWindow;
        private Win32WindowStartupVisibility? startupWindowVisibility;
        private GodotWindowAppearance? windowAppearance;
        private bool previousRenderLoopEnabled;
        private bool renderLoopOwned;
        private IGodotGraphicsHost? graphicsSubmission;
        private SocketsCompressedHttpTransport? compressedHttp;
        private GodotNativeLibraryResolver? nativeLibraries;
        private bool clientSizeChangedBound;
        private bool clientSizeChangePending;
        private bool started;
        private bool disposed;

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
            previousRenderLoopEnabled = RenderingServer.RenderLoopEnabled;
            RenderingServer.RenderLoopEnabled = false;
            renderLoopOwned = true;
            ProcessMode = ProcessModeEnum.Always;
            GetTree().AutoAcceptQuit = false;
            Window window = GetWindow();
            if (OperatingSystem.IsWindows() &&
                !string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase))
            {
                startupWindowVisibility = new Win32WindowStartupVisibility(new IntPtr(
                    DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, window.GetWindowId())));
                string nativeIcon = ProjectSettings.GetSetting("application/config/windows_native_icon", string.Empty)
                    .AsString();
                if (nativeIcon.Length != 0) DisplayServer.SetNativeIcon(nativeIcon);
                windowAppearance = new GodotWindowAppearance(new IntPtr(
                    DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, window.GetWindowId())));
            }

            window.Borderless = false;
            window.Unfocusable = false;
            window.Transparent = false;
            GetViewport().TransparentBg = false;
            window.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
            string[] userArguments = OS.GetCmdlineUserArgs();
            GameProfile profile = GameProfileSelection.Resolve(sourceProfile, userArguments);
            bool editorHost = OS.HasFeature("editor");
            string applicationDirectory = editorHost
                ? Path.GetFullPath(ProjectSettings.GlobalizePath("res://"))
                : Path.GetDirectoryName(OS.GetExecutablePath())!;
            var contentPaths = new global::Celeste.Hosting.Runtime.Content.GameContentPaths(editorHost
                ? Path.Combine(applicationDirectory, "..", "Content")
                : applicationDirectory);
            string contentRoot = contentPaths.Game;
            string frameworkTimeResources = contentPaths.FrameworkTimeResources;
            GD.Print("Celeste startup: profile=" + profile + ", contentRoot=" + contentRoot +
                     ", applicationDirectory=" + applicationDirectory);
            string physicalOwnerAssemblyPath = GodotAssemblyPathIO.GetPhysicalPath(typeof(GameApplication).Assembly);
            if (!Directory.Exists(contentRoot))
            {
                throw new DirectoryNotFoundException("The deployed game content is missing: " + contentRoot);
            }

            nativeLibraries = new GodotNativeLibraryResolver(Path.Combine(applicationDirectory, "Native",
                GodotNativeLibraryResolver.RuntimeIdentifier));
            nativeLibraries.Bind();
            displays = new NativeSourceDisplayIO();
            physicalWindow = new GodotSourceWindowIO(window, displays);
            inputDevices = new GodotSourceInputDevices(window, physicalWindow);
            GodotGraphicsBackend selectedBackend =
                GodotGraphicsBackendSelection.Resolve(graphicsBackend, userArguments);
            graphicsSubmission = selectedBackend switch
            {
                GodotGraphicsBackend.Direct3D11 => new GodotNativeGraphicsSubmission(physicalWindow,
                    Thread.CurrentThread.ManagedThreadId),
                GodotGraphicsBackend.Direct3D12 => new GodotDirect3D12GraphicsHost(physicalWindow,
                    physicalWindow.Handle),
                GodotGraphicsBackend.Vulkan => new GodotVulkanGraphicsHost(physicalWindow, window),
                _ => throw new ArgumentOutOfRangeException(nameof(graphicsBackend))
            };
            GD.Print("Celeste graphics: ", selectedBackend);
            var graphicsFactory = graphicsSubmission.DeviceFactory;
            var sdl = new NativeSourceSDLRuntimeDevice();
            var runtime = new GodotSourceRuntimeIO(physicalWindow, physicalOwnerAssemblyPath,
                applicationDirectory, new[] { OS.GetExecutablePath() }.Concat(userArguments).ToArray(),
                typeof(GodotBootstrap).GetMethod(nameof(_Ready),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null) ??
                throw new MissingMethodException(typeof(GodotBootstrap).FullName, nameof(_Ready)),
                DisplayServer.IsTouchscreenAvailable, sdl.SetHint);
            var clock = new SourceStopwatchGameClockDevice();
            if (profile == GameProfile.InstalledEverest)
            {
                compressedHttp = new SocketsCompressedHttpTransport();
            }

            var physical = new GamePlatformServices(physicalOwnerAssemblyPath, contentRoot,
                physicalWindow, graphicsFactory, displays,
                new ProjectedContentSource(new DirectoryContentSource(contentRoot),
                    new System.Collections.Generic.Dictionary<string, string> { { ".meta", ".atlas.bytes" } },
                    new[] { ".meta" }),
                new NativeSourceFnaImageCodecDevice(), inputDevices.Keyboard, inputDevices.MouseTouch,
                inputDevices.GamePads,
                clock, clock, new NativeSourceRandomSeeds(), new NativeSourceFAudioIOFactory(),
                new NativeSourceMicrophoneIO(), new NativeSourceTheoraIO(), runtime, inputDevices,
                new NativeSourceFrameworkTimeResources(frameworkTimeResources),
                runtime, runtime, sdl, runtime, runtime, null, runtime, runtime,
                profile == GameProfile.InstalledEverest ? new ProcessSourceEverestSplashIO() : null,
                new NativeSourceConsoleModeIO(), graphicsSubmission.DrawTransport, runtime, inputDevices,
                new ManagedSourceRuntimeDiagnostics(new NativeSourceRuntimeMemoryCapacity()),
                null, inputDevices, compressedHttp,
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
            process = GameApplicationFactory.Create(profile, physical);
            if (process == null)
            {
                GetTree().Quit();
                return;
            }

            if (profile == GameProfile.InstalledEverest)
            {
                process.BindSourceInitializationSaveLinkIO(new SourceDirectoryLinkTargetIO());
            }

            process.AudioSession.Device.AudioOutputCreated += noSound => GD.Print("FMOD NoSound=", noSound);
            graphicsSubmission.FramePresented += ShowSourceWindow;
            inputBridge = new GodotSourceInputBridge(inputDevices, process);
            clock.BindPollEvents(PollSourceEvents);
            process.ActualGame.IsActive = window.HasFocus();
            window.SizeChanged += NotifyClientSizeChanged;
            clientSizeChangedBound = true;
            CallDeferred(nameof(StartSourceRun));
        }

        private void ShowSourceWindow()
        {
            graphicsSubmission!.FramePresented -= ShowSourceWindow;
            startupWindowVisibility?.Complete();
            process!.ActualGame.IsActive = GetWindow().HasFocus();
            // Only D3D11 replays here; shared window backends own their acquisition/presentation sequence.
            graphicsSubmission.WindowShown();
        }

        private void StartSourceRun()
        {
            if (disposed || process == null)
            {
                return;
            }

            try
            {
                process.ActualGame.IsActive = GetWindow().HasFocus();
                process.ActualGame.StartSourceHostRun();
                started = true;
                graphicsSubmission!.Pump();
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
            if (disposed || process == null || !started)
            {
                return;
            }

            try
            {
                graphicsSubmission!.Pump();
                if (process.ActualGame.SourceHostRunOpen)
                {
                    process.ActualGame.ContinueSourceHostRun();
                }

                graphicsSubmission.Pump();
                if (!process.ActualGame.SourceHostRunOpen)
                {
                    GetTree().Quit();
                }
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
                process?.WriteSourceExceptionToLog(exception);
            }
            finally
            {
                GD.PushError(exception.ToString());
            }
        }

        private void AbortSourceRun()
        {
            graphicsSubmission?.AbortUnsubmittedDraws();
            started = false;
            SetProcess(false);
            process?.ActualGame.GameLoop.Exit();
            GetTree().Quit(1);
        }

        public override void _Input(InputEvent input)
        {
            if (!disposed)
            {
                inputBridge?.HandleEvent(input);
            }
        }

        public override void _Notification(int what)
        {
            if (disposed || process == null)
            {
                return;
            }

            if (what == NotificationWMCloseRequest)
            {
                process.ActualGame.GameLoop.Exit();
            }
            else if (what == NotificationWMWindowFocusIn || what == NotificationWMWindowFocusOut)
            {
                process.ActualGame.IsActive = what == NotificationWMWindowFocusIn;
            }
        }

        private void NotifyClientSizeChanged()
        {
            // Godot emits SizeChanged synchronously from programmatic window resizing.
            // Deliver the physical event at the source event-poll boundary, after
            // GraphicsDeviceManager has completed its create/reset transaction.
            if (!disposed)
            {
                clientSizeChangePending = true;
            }
        }

        private void PollSourceEvents()
        {
            if (clientSizeChangePending && process!.ActualGame.Window is SourceHostGameWindow window)
            {
                clientSizeChangePending = false;
                window.NotifyClientSizeChanged();
            }

            inputBridge!.PollEvents();
        }

        private void PumpSourceShutdown()
        {
            graphicsSubmission?.Pump();
            if (process!.SourceProfile == GameProfile.InstalledEverest)
            {
                process.MainThreadProcess.Update(null);
            }

            graphicsSubmission?.Pump();
        }

        private void ReleaseSourceHostResources(bool releaseNativeLibraries)
        {
            try
            {
                startupWindowVisibility?.Dispose();
                startupWindowVisibility = null;
                windowAppearance?.Dispose();
                windowAppearance = null;
                compressedHttp?.Dispose();
                inputBridge?.Dispose();
                inputDevices?.Dispose();
                displays?.Dispose();
            }
            finally
            {
                try
                {
                    if (graphicsSubmission != null)
                    {
                        graphicsSubmission.FramePresented -= ShowSourceWindow;
                        graphicsSubmission.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        if (releaseNativeLibraries) nativeLibraries?.Dispose();
                    }
                    finally
                    {
                        if (renderLoopOwned)
                        {
                            RenderingServer.RenderLoopEnabled = previousRenderLoopEnabled;
                            renderLoopOwned = false;
                        }
                    }
                }
            }
        }

        public override void _ExitTree()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (clientSizeChangedBound)
            {
                GetWindow().SizeChanged -= NotifyClientSizeChanged;
                clientSizeChangedBound = false;
            }

            bool threadsCompleted = process == null;
            bool sourceGameDisposed = process == null;
            try
            {
                if (process != null)
                {
                    process.ActualGame.GameLoop.Exit();
                    try
                    {
                        process.WaitSourceBackgroundThreads(PumpSourceShutdown);
                        process.ActualGame.FinishSourceHostRun();
                    }
                    finally
                    {
                        process.WaitSourceBackgroundThreads(PumpSourceShutdown);
                        threadsCompleted = true;
                        try
                        {
                            graphicsSubmission?.StopPresenting();
                        }
                        finally
                        {
                            process.ActualGame.Dispose();
                            sourceGameDisposed = true;
                        }
                    }
                }
                else
                {
                    graphicsSubmission?.StopPresenting();
                }
            }
            finally
            {
                if (threadsCompleted) ReleaseSourceHostResources(sourceGameDisposed);
            }
        }
    }
}
