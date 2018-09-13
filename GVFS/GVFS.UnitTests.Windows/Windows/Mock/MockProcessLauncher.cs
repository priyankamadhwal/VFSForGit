﻿using static GVFS.CommandLine.UpgradeVerb;

namespace GVFS.UnitTests.Windows.Upgrader
{
    public class MockProcessLauncher : ProcessLauncher
    {
        private int exitCode;
        private bool hasExited;
        private bool startResult;

        public MockProcessLauncher(
            int exitCode,
            bool hasExited,
            bool startResult) : base()
        {
            this.exitCode = exitCode;
            this.hasExited = hasExited;
            this.startResult = startResult;
        }

        public bool IsLaunched { get; private set; }

        public string LaunchPath { get; private set; }

        public override bool HasExited
        {
            get { return this.hasExited; }
        }

        public override int ExitCode
        {
            get { return this.exitCode; }
        }

        public override bool Start(string path)
        {
            this.LaunchPath = path;
            this.IsLaunched = true;

            return this.startResult;
        }
    }
}
