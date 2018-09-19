using GVFS.Common;
using GVFS.Common.Git;
using GVFS.Common.Tracing;
using System;
using System.IO;

namespace GVFS.Upgrader
{
    public class UpgradeOrchestrator
    {
        private const EventLevel DefaultEventLevel = EventLevel.Informational;

        private ProductUpgrader upgrader;
        private ITracer tracer;
        private InstallerPreRunChecker preRunChecker;
        private TextWriter output;
        private TextReader input;
        private bool remount;
        private bool shouldExitOnError;

        public UpgradeOrchestrator(
            ProductUpgrader upgrader, 
            ITracer tracer,
            InstallerPreRunChecker preRunChecker,
            TextReader input,
            TextWriter output,
            bool shouldExitOnError)
        {
            this.upgrader = upgrader;
            this.tracer = tracer;
            this.preRunChecker = preRunChecker;
            this.output = output;
            this.input = input;
            this.remount = false;
            this.shouldExitOnError = shouldExitOnError;
            this.ExitCode = ReturnCode.Success;
        }

        public UpgradeOrchestrator()
        {
            string logFilePath = GVFSEnlistment.GetNewGVFSLogFileName(
                ProductUpgrader.GetLogDirectoryPath(),
                GVFSConstants.LogFileTypes.UpgradeProcess);
            JsonTracer jsonTracer = new JsonTracer(GVFSConstants.GVFSEtwProviderName, "UpgradeProcess");
            jsonTracer.AddLogFileEventListener(
                logFilePath,
                DefaultEventLevel,
                Keywords.Any);

            this.tracer = jsonTracer;
            this.preRunChecker = new InstallerPreRunChecker(this.tracer);
            this.upgrader = new ProductUpgrader(ProcessHelper.GetCurrentProcessVersion(), this.tracer);
            this.output = Console.Out;
            this.input = Console.In;
            this.remount = false;
            this.shouldExitOnError = false;
            this.ExitCode = ReturnCode.Success;
        }

        public ReturnCode ExitCode { get; private set; }

        public void Execute()
        {
            string error = null;

            ProductUpgrader.RingType ring = ProductUpgrader.RingType.Invalid;

            if (!this.TryLoadUpgradeRing(out ring, out error) || ring == ProductUpgrader.RingType.None)
            {
                string message = ring == ProductUpgrader.RingType.None ?
                    GVFSConstants.UpgradeVerbMessages.NoneRingConsoleAlert :
                    GVFSConstants.UpgradeVerbMessages.InvalidRingConsoleAlert;
                this.output.WriteLine(message);

                if (ring == ProductUpgrader.RingType.None)
                {
                    this.output.WriteLine(GVFSConstants.UpgradeVerbMessages.SetUpgradeRingCommand);
                }
            }
            else
            {
                try
                {
                    Version newVersion = null;
                    if (!this.TryRunUpgradeInstall(out newVersion, out error))
                    {
                        this.ExitCode = ReturnCode.GenericError;
                    }
                }
                finally
                {
                    string remountError = null;
                    if (!this.TryRemountRepositories(out remountError))
                    {
                        remountError = Environment.NewLine + "WARNING: " + remountError;
                        this.output.WriteLine(remountError);
                        this.ExitCode = ReturnCode.Success;
                    }

                    this.DeletedDownloadedAssets();
                }
            }

            if (this.ExitCode == ReturnCode.GenericError)
            {
                error = Environment.NewLine + "ERROR: " + error;
                this.output.WriteLine(error);
            }
            else
            {
                this.output.WriteLine(Environment.NewLine + "Upgrade completed successfully!");
            }

            if (this.input == Console.In)
            {
                this.output.WriteLine("Press Enter to exit.");
                this.input.ReadLine();
            }
            
            if (this.shouldExitOnError)
            {
                Environment.Exit((int)this.ExitCode);
            }
        }

        private bool LaunchInsideSpinner(Func<bool> method, string message)
        {
            return ConsoleHelper.ShowStatusWhileRunning(
                method,
                message,
                this.output,
                this.output == Console.Out && !GVFSPlatform.Instance.IsConsoleOutputRedirectedToFile(),
                null);
        }

        private bool TryLoadUpgradeRing(out ProductUpgrader.RingType ring, out string consoleError)
        {
            bool loaded = false;
            if (!this.upgrader.TryLoadRingConfig(out consoleError))
            {
                EventMetadata metadata = new EventMetadata();
                metadata.Add("Upgrade Step", nameof(this.TryLoadUpgradeRing));
                metadata.Add("Load Error", consoleError);
                this.tracer.RelatedError(metadata, $"{nameof(this.TryLoadUpgradeRing)} failed.");
                this.ExitCode = ReturnCode.GenericError;
            }
            else
            {
                consoleError = null;
                loaded = true;
            }
            
            ring = this.upgrader.Ring;
            return loaded;
        }

        private bool TryRunUpgradeInstall(out Version newVersion, out string consoleError)
        {
            newVersion = null;

            Version newGVFSVersion = null;
            GitVersion newGitVersion = null;
            string errorMessage = null;
            if (!this.LaunchInsideSpinner(
                () =>
                {
                    if (!this.TryCheckIfUpgradeAvailable(out newGVFSVersion, out errorMessage) ||
                        !this.TryGetNewGitVersion(out newGitVersion, out errorMessage))
                    {
                        return false;
                    }

                    this.LogInstalledVersionInfo();
                    this.LogVersionInfo(newGVFSVersion, newGitVersion, "Available Version");

                    this.preRunChecker.CommandToRerun = "gvfs upgrade --confirm";
                    if (!this.preRunChecker.TryRunPreUpgradeChecks(out errorMessage))
                    {
                        return false;
                    }

                    if (!this.TryDownloadUpgrade(newGVFSVersion, out errorMessage))
                    {
                        return false;
                    }

                    return true;
                },
                "Downloading"))
            {
                consoleError = errorMessage;
                return false;
            }

            if (!this.LaunchInsideSpinner(
                () =>
                {
                    if (!this.preRunChecker.TryUnmountAllGVFSRepos(out errorMessage))
                    {
                        return false;
                    }

                    this.remount = true;

                    return true;
                },
                "Unmounting repositories"))
            {
                consoleError = errorMessage;
                return false;
            }

            if (!this.LaunchInsideSpinner(
                () =>
                {
                    if (!this.TryInstallGitUpgrade(newGitVersion, out errorMessage))
                    {
                        return false;
                    }

                    return true;
                },
                $"Installing Git version: {newGitVersion}"))
            {
                consoleError = errorMessage;
                return false;
            }

            if (!this.LaunchInsideSpinner(
                () =>
                {
                    if (!this.TryInstallGVFSUpgrade(newGVFSVersion, out errorMessage))
                    {
                        return false;
                    }

                    return true;
                },
                $"Installing GVFS version: {newGVFSVersion}"))
            {
                consoleError = errorMessage;
                return false;
            }

            this.LogVersionInfo(newGVFSVersion, newGitVersion, "Newly Installed Version");

            newVersion = newGVFSVersion;
            consoleError = null;
            return true;
        }
        
        private bool TryRemountRepositories(out string consoleError)
        {
            string errorMessage = string.Empty;
            if (this.remount && !this.LaunchInsideSpinner(
                () =>
                {
                    string remountError;
                    if (!this.preRunChecker.TryMountAllGVFSRepos(out remountError))
                    {
                        EventMetadata metadata = new EventMetadata();
                        metadata.Add("Upgrade Step", nameof(this.TryRemountRepositories));
                        metadata.Add("Remount Error", remountError);
                        this.tracer.RelatedError(metadata, $"{nameof(this.preRunChecker.TryMountAllGVFSRepos)} failed.");
                        errorMessage += remountError;
                        return false;
                    }

                    return true;
                },
                "Mounting repositories"))
            {
                consoleError = errorMessage;
                return false;
            }

            consoleError = null;
            return true;
        }

        private void DeletedDownloadedAssets()
        {
            string downloadsCleanupError;
            if (!this.upgrader.TryCleanup(out downloadsCleanupError))
            {
                EventMetadata metadata = new EventMetadata();
                metadata.Add("Upgrade Step", nameof(this.DeletedDownloadedAssets));
                metadata.Add("Download cleanup error", downloadsCleanupError);
                this.tracer.RelatedError(metadata, $"{nameof(this.DeletedDownloadedAssets)} failed.");
            }
        }

        private bool TryGetNewGitVersion(out GitVersion gitVersion, out string consoleError)
        {
            gitVersion = null;

            using (ITracer activity = this.tracer.StartActivity(nameof(this.TryGetNewGitVersion), EventLevel.Informational))
            {
                if (!this.upgrader.TryGetGitVersion(out gitVersion, out consoleError))
                {
                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("Upgrade Step", nameof(this.TryGetNewGitVersion));
                    this.tracer.RelatedError(metadata, $"{nameof(this.upgrader.TryGetGitVersion)} failed. {consoleError}");
                    return false;
                }

                activity.RelatedInfo("Successfully read Git version {0}", gitVersion);
            }

            return true;
        }

        private bool TryCheckIfUpgradeAvailable(out Version newestVersion, out string consoleError)
        {
            newestVersion = null;

            using (ITracer activity = this.tracer.StartActivity(nameof(this.TryCheckIfUpgradeAvailable), EventLevel.Informational))
            {
                if (!this.upgrader.TryGetNewerVersion(out newestVersion, out consoleError))
                {
                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("Upgrade Step", nameof(this.TryCheckIfUpgradeAvailable));
                    this.tracer.RelatedError(metadata, $"{nameof(this.upgrader.TryGetNewerVersion)} failed. {consoleError}");
                    return false;
                }

                if (newestVersion == null)
                {
                    consoleError = "No upgrades available in ring: " + this.upgrader.Ring;
                    this.tracer.RelatedInfo("No new upgrade releases available");
                    return false;
                }

                activity.RelatedInfo("Successfully checked for new release. {0}", newestVersion);
            }
            
            return true;
        }

        private bool TryDownloadUpgrade(Version version, out string consoleError)
        {
            using (ITracer activity = this.tracer.StartActivity(
                $"{nameof(this.TryDownloadUpgrade)}({version.ToString()})", 
                EventLevel.Informational))
            {
                if (!this.upgrader.TryDownloadNewestVersion(out consoleError))
                {
                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("Upgrade Step", nameof(this.TryDownloadUpgrade));
                    this.tracer.RelatedError(metadata, $"{nameof(this.upgrader.TryDownloadNewestVersion)} failed. {consoleError}");
                    return false;
                }

                activity.RelatedInfo("Successfully downloaded version: " + version.ToString());
            }

            return true;
        }

        private bool TryInstallGitUpgrade(GitVersion version, out string consoleError)
        {
            bool installSuccess = false;
            using (ITracer activity = this.tracer.StartActivity(
                $"{nameof(this.TryInstallGitUpgrade)}({version.ToString()})",
                EventLevel.Informational))
            {                
                if (!this.upgrader.TryRunGitInstaller(out installSuccess, out consoleError) ||
                    !installSuccess)
                {
                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("Upgrade Step", nameof(this.TryInstallGitUpgrade));
                    this.tracer.RelatedError(metadata, $"{nameof(this.upgrader.TryRunGitInstaller)} failed. {consoleError}");
                    return false;
                }

                activity.RelatedInfo("Successfully installed Git version: " + version.ToString());
            }

            return installSuccess;
        }

        private bool TryInstallGVFSUpgrade(Version version, out string consoleError)
        {
            bool installSuccess = false;
            using (ITracer activity = this.tracer.StartActivity(
                $"{nameof(this.TryInstallGVFSUpgrade)}({version.ToString()})",
                EventLevel.Informational))
            {
                if (!this.upgrader.TryRunGVFSInstaller(out installSuccess, out consoleError) ||
                !installSuccess)
                {
                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("Upgrade Step", nameof(this.TryInstallGVFSUpgrade));
                    this.tracer.RelatedError(metadata, $"{nameof(this.upgrader.TryRunGVFSInstaller)} failed. {consoleError}");
                    return false;
                }

                activity.RelatedInfo("Successfully installed GVFS version: " + version.ToString());
            }

            return installSuccess;
        }

        private void LogVersionInfo(
            Version gvfsVersion,
            GitVersion gitVersion,
            string message)
        {
            EventMetadata metadata = new EventMetadata();
            metadata.Add(nameof(gvfsVersion), gvfsVersion.ToString());
            metadata.Add(nameof(gitVersion), gitVersion.ToString());

            this.tracer.RelatedEvent(EventLevel.Informational, message, metadata);
        }

        private void LogInstalledVersionInfo()
        {
            EventMetadata metadata = new EventMetadata();
            string installedGVFSVersion = ProcessHelper.GetCurrentProcessVersion();
            metadata.Add(nameof(installedGVFSVersion), installedGVFSVersion);

            GitVersion installedGitVersion = null;
            string error = null;
            if (GitProcess.TryGetVersion(
                out installedGitVersion,
                out error))
            {
                metadata.Add(nameof(installedGitVersion), installedGitVersion.ToString());
            }

            this.tracer.RelatedEvent(EventLevel.Informational, "Installed Version", metadata);
        }
    }
}