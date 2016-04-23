﻿using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
using EnvDTE;
using Microsoft.VisualStudio.Text;

namespace IncludeToolbox
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class TryAndErrorRemoval
    {
        public delegate void FinishedEvent(int numRemovedIncludes);
        public event FinishedEvent OnFileFinished;

        public static bool WorkInProgress { get; private set; }

        private volatile bool lastBuildSuccessful;
        private AutoResetEvent outputWaitEvent = new AutoResetEvent(false);
        private const int timeoutMS = 30000; // 30 seconds

        public static VCFileConfiguration GetFileConfig(EnvDTE.Document document, out string reasonForFailure, out bool isHeader)
        {
            isHeader = false;

            if (document == null)
            {
                reasonForFailure = "No document.";
                return null;
            }

            var project = document.ProjectItem?.ContainingProject;
            VCProject vcProject = project.Object as VCProject;
            if (vcProject == null)
            {
                reasonForFailure = "The given document does not belong to a VC++ Project.";
                return null;
            }

            VCFile vcFile = document.ProjectItem?.Object as VCFile;
            if (vcFile == null)
            {
                reasonForFailure = "The given document is not a VC++ file.";
                return null;
            }

            isHeader = vcFile.FileType == Microsoft.VisualStudio.VCProjectEngine.eFileType.eFileTypeCppHeader;

            IVCCollection fileConfigCollection = vcFile.FileConfigurations;
            VCFileConfiguration fileConfig = fileConfigCollection?.Item(vcProject.ActiveConfiguration.Name);
            if (fileConfig == null)
            {
                reasonForFailure = "Failed to retrieve file config from document.";
                return null;
            }

            reasonForFailure = "";
            return fileConfig;
        }


        public void PerformTryAndErrorRemoval(EnvDTE.Document document)
        {
            if (document == null)
                return;

            string errorMessage;
            bool isHeader;
            var fileConfig = GetFileConfig(document, out errorMessage, out isHeader);
            if (fileConfig == null)
            {
                Output.Instance.WriteLine(errorMessage);
                return;
            }
            if (isHeader)
            {
                Output.Instance.WriteLine("Try and error include removal does not work on Headers.");
                return;
            }

            PerformTryAndErrorRemoval(document, fileConfig);
        }

        public void PerformTryAndErrorRemoval(EnvDTE.Document document, VCFileConfiguration fileConfig)
        {
            if (document == null || fileConfig == null)
                return;

            if (WorkInProgress)
            {
                Output.Instance.ErrorMsg("Try and error include removal already in progress!");
                return;
            }
            WorkInProgress = true;

            // Start wait dialog.
            IVsThreadedWaitDialog2 progressDialog = null;
            {
                var dialogFactory = Package.GetGlobalService(typeof(SVsThreadedWaitDialogFactory)) as IVsThreadedWaitDialogFactory;
                if (dialogFactory == null)
                {
                    Output.Instance.WriteLine("Failed to get Dialog Factory for wait dialog.");
                    return;
                }
                dialogFactory.CreateInstance(out progressDialog);
                if (progressDialog == null)
                {
                    Output.Instance.WriteLine("Failed to get create wait dialog.");
                    return;
                }
                string waitMessage = $"Parsing '{document.Name}' ... ";
                progressDialog.StartWaitDialogWithPercentageProgress(
                    szWaitCaption: "Include Toolbox - Running Try & Error Include Removal",
                    szWaitMessage: waitMessage,
                    szProgressText: null,
                    varStatusBmpAnim: null,
                    szStatusBarText: "Running Try & Error Removal - " + waitMessage,
                    fIsCancelable: true,
                    iDelayToShowDialog: 0,
                    iTotalSteps: 20,    // Will be replaced.
                    iCurrentStep: 0);
            }

            // Extract all includes.
            IncludeFormatter.IncludeLineInfo[] documentLines = null;
            ITextBuffer textBuffer = null;
            try
            {
                document.Activate();
                var documentTextView = VSUtils.GetCurrentTextViewHost();
                textBuffer = documentTextView.TextView.TextBuffer;
                string documentText = documentTextView.TextView.TextSnapshot.GetText();
                documentLines = IncludeFormatter.IncludeLineInfo.ParseIncludes(documentText, false, null);
            }
            catch (Exception ex)
            {
                Output.Instance.WriteLine("Unexpected error: {0}", ex);
                progressDialog.EndWaitDialog();
                return;
            }
            int numIncludes = documentLines.Count(x => x.LineType != IncludeFormatter.IncludeLineInfo.Type.NoInclude);



            // Hook into build events.
            document.DTE.Events.BuildEvents.OnBuildProjConfigDone += OnBuildConfigFinished;
            document.DTE.Events.BuildEvents.OnBuildDone += OnBuildFinished;


            // The rest runs in a separate thread sicne the compile function is non blocking and we want to use BuildEvents
            // We are not using Task, since we want to make use of WaitHandles - using this together with Task is a bit more complicated to get right.
            outputWaitEvent.Reset();
            new System.Threading.Thread(() =>
            {
                int numRemovedIncludes = 0;

                try
                {
                    int currentStep = 0;

                    // For ever include line..
                    for (int line = documentLines.Length - 1; line >= 0; --line)
                    {
                        if (documentLines[line].LineType == IncludeFormatter.IncludeLineInfo.Type.NoInclude)
                            continue;

                        // Update progress.
                        string waitMessage = $"Removing #includes from '{document.Name}'";
                        string progressText = $"Trying to remove '{documentLines[line].IncludeContent}' ...";
                        bool canceled = false;
                        progressDialog.UpdateProgress(
                            szUpdatedWaitMessage: waitMessage,
                            szProgressText: progressText,
                            szStatusBarText: "Running Try & Error Removal - " + waitMessage + " - " + progressText,
                            iCurrentStep: currentStep + 1,
                            iTotalSteps: numIncludes + 1,
                            fDisableCancel: false,
                            pfCanceled: out canceled);
                        if (canceled)
                            break;

                        ++currentStep;

                        // Remove include - this needs to be done on the main thread.
                        int currentLine = line;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            using (var edit = textBuffer.CreateEdit())
                            {
                                edit.Delete(edit.Snapshot.Lines.ElementAt(currentLine).ExtentIncludingLineBreak);
                                edit.Apply();
                            }
                            outputWaitEvent.Set();
                        });
                        outputWaitEvent.WaitOne();

                        // Compile - In rare cases VS tells us that we are still building which should not be possible because we have received OnBuildFinished
                        // As a workaround we just try again a few times.
                        {
                            const int maxNumCompileAttempts = 3;
                            bool fail = false;
                            for (int numCompileFails = 0; numCompileFails < maxNumCompileAttempts; ++numCompileFails)
                            {
                                try
                                {
                                    fileConfig.Compile(true, false); // WaitOnBuild==true always fails.    
                                }
                                catch (Exception e)
                                {
                                    if (numCompileFails == maxNumCompileAttempts - 1)
                                    {
                                        Output.Instance.WriteLine("Compile Failed:\n{0}", e);
                                        fail = true;
                                    }
                                    else
                                    {
                                        // Try again.
                                        System.Threading.Thread.Sleep(1);
                                        continue;
                                    }
                                }
                                break;
                            }
                            if (fail) break;
                        }

                        // Wait till woken.
                        bool noTimeout = outputWaitEvent.WaitOne(timeoutMS);

                        // Undo removal if compilation failed.
                        if (!noTimeout || !lastBuildSuccessful)
                        {
                            Output.Instance.WriteLine("Could not remove #include: '{0}'", documentLines[line].IncludeContent);
                            document.Undo();
                            if (!noTimeout)
                            {
                                Output.Instance.ErrorMsg("Compilation of {0} timeouted!", document.Name);
                                break;
                            }
                        }
                        else
                        {
                            Output.Instance.WriteLine("Successfully removed #include: '{0}'", documentLines[line].IncludeContent);
                            ++numRemovedIncludes;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Output.Instance.WriteLine("Unexpected error: {0}", ex);
                }
                finally
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // Close Progress bar.
                        progressDialog.EndWaitDialog();

                        // Remove build hook again.
                        document.DTE.Events.BuildEvents.OnBuildDone -= OnBuildFinished;
                        document.DTE.Events.BuildEvents.OnBuildProjConfigDone -= OnBuildConfigFinished;

                        // Message.
                        Output.Instance.WriteLine("Removed {0} #include directives from '{1}'", numRemovedIncludes, document.Name);

                        // Notify that we are done.
                        WorkInProgress = false;
                        OnFileFinished?.Invoke(numRemovedIncludes);
                    });
                }
            }).Start();
        }

        private void OnBuildFinished(vsBuildScope scope, vsBuildAction action)
        {
            outputWaitEvent.Set();
        }

        private void OnBuildConfigFinished(string project, string projectConfig, string platform, string solutionConfig, bool success)
        {
            lastBuildSuccessful = success;
        }
    }
}