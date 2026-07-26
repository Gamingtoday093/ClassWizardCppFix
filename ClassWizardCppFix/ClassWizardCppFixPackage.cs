global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using System;
global using Task = System.Threading.Tasks.Task;
using ClassWizardCppFix.Services;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClassWizardCppFix
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.ClassWizardCppFixString)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.VCProject_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class ClassWizardCppFixPackage : ToolkitPackage
    {
        private ProjectDocumentsEventSink FileEventHandler { get; } = new();

        private IVsMonitorSelection SelectionMonitor { get; set; }
        private IVsRunningDocumentTable RunningDocuments { get; set; }
        private IVsUIShellOpenDocument OpenDocument { get; set; }

        private static readonly string[] ClassFileExtensions = [ ".h", ".cpp" ];
        private string OtherClassFilePath { get; set; } = string.Empty;
        private bool TransferingFiles { get; set; } = false;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            SelectionMonitor = await this.GetServiceAsync<SVsShellMonitorSelection, IVsMonitorSelection>();
            RunningDocuments = await this.GetServiceAsync<SVsRunningDocumentTable, IVsRunningDocumentTable>();
            OpenDocument = await this.GetServiceAsync<SVsUIShellOpenDocument, IVsUIShellOpenDocument>();

            FileEventHandler.OnFileAdded += OnClassFileAdded;
            var fileTracker = await this.GetServiceAsync<SVsTrackProjectDocuments, IVsTrackProjectDocuments2>();
            fileTracker.AdviseTrackProjectDocumentsEvents(FileEventHandler, out _);
        }

        private void OnClassFileAdded(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (TransferingFiles) return;
            if (!ClassFileExtensions.Contains(Path.GetExtension(filePath))) return;

            if (string.IsNullOrEmpty(OtherClassFilePath))
            {
                OtherClassFilePath = filePath;
                return;
            }

            if (Path.GetExtension(filePath) == Path.GetExtension(OtherClassFilePath))
                goto ClearOtherClassFile;

            if (Path.GetDirectoryName(filePath) != Path.GetDirectoryName(OtherClassFilePath))
                goto ClearOtherClassFile;

            SelectionMonitor.GetCurrentSelection(
                out IntPtr hierarchyPtr,
                out uint selectionItemid,
                out _,
                out IntPtr ppSC);

            IVsHierarchy hierarchy;
            IVsProject4 project;
            try
            {
                hierarchy = Marshal.GetObjectForIUnknown(hierarchyPtr) as IVsHierarchy;
                Debug.Assert(hierarchy != null);
                project = hierarchy as IVsProject4;
                Debug.Assert(project != null);
            }
            finally
            {
                Marshal.Release(hierarchyPtr);
                if (ppSC != IntPtr.Zero) Marshal.Release(ppSC);
            }

            hierarchy.GetCanonicalName(selectionItemid, out string selectedPath);
            if (!File.GetAttributes(selectedPath).HasFlag(FileAttributes.Directory))
                selectedPath = Path.GetDirectoryName(selectedPath);

            if (Path.GetDirectoryName(filePath) == selectedPath || Path.GetDirectoryName(OtherClassFilePath) == selectedPath)
                goto ClearOtherClassFile;

            string newFilePath = Path.Combine(selectedPath, Path.GetFileName(filePath));
            string newOtherClassFile = Path.Combine(selectedPath, Path.GetFileName(OtherClassFilePath));

            if (File.Exists(newFilePath) || File.Exists(newOtherClassFile))
                goto ClearOtherClassFile;

            string otherClassFile = OtherClassFilePath;
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Yield(); // Delay Until Next *Frame*
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                var priority = new VSDOCUMENTPRIORITY[1];
                project.IsDocumentInProject(filePath, out _, priority, out uint fileItemId);
                if (!CloseAndSaveDocumentSilently(filePath))
                    SaveDocumentSilently(filePath);

                project.IsDocumentInProject(otherClassFile, out _, priority, out uint otherFileItemId);
                if (!CloseAndSaveDocumentSilently(otherClassFile))
                    SaveDocumentSilently(filePath);

                TransferingFiles = true;
                try
                {
                    File.Move(filePath, newFilePath);
                    File.Move(otherClassFile, newOtherClassFile);

                    var result = new VSADDRESULT[1];
                    project.AddItem(selectionItemid, VSADDITEMOPERATION.VSADDITEMOP_OPENFILE, newOtherClassFile, 0, Array.Empty<string>(), IntPtr.Zero, result);
                    project.AddItem(selectionItemid, VSADDITEMOPERATION.VSADDITEMOP_OPENFILE, newFilePath, 0, Array.Empty<string>(), IntPtr.Zero, result);

                    project.RemoveItem(0, fileItemId, out _);
                    project.RemoveItem(0, otherFileItemId, out _);
                }
                finally
                {
                    TransferingFiles = false;
                }

                #region Open Header File
                Guid logicalView = VSConstants.LOGVIEWID.Primary_guid;
                string headerFile = Path.GetExtension(newFilePath) == ".h" ? newFilePath : newOtherClassFile;
                int openHR = OpenDocument.OpenDocumentViaProject(headerFile, ref logicalView, out _, out _, out _, out var frame);
                if (openHR == VSConstants.S_OK) frame?.Show();
                #endregion // Open Header File
            });

        ClearOtherClassFile:
            OtherClassFilePath = string.Empty;
        }

        private bool CloseAndSaveDocumentSilently(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (VsShellUtilities.IsDocumentOpen(ServiceProvider.GlobalProvider, filePath, Guid.Empty, out _, out _, out IVsWindowFrame frame))
                return frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_SaveIfDirty) == VSConstants.S_OK;

            return false;
        }

        private bool SaveDocumentSilently(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            RunningDocuments.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, filePath, out _, out _, out IntPtr docDataPtr, out _);
            try
            {
                if (Marshal.GetObjectForIUnknown(docDataPtr) is IVsPersistDocData persistDocData)
                {
                    int saveHR = persistDocData.SaveDocData(
                        VSSAVEFLAGS.VSSAVE_SilentSave,
                        out _,
                        out int canceled);

                    return saveHR == VSConstants.S_OK && canceled == 0;
                }
            }
            finally
            {
                Marshal.Release(docDataPtr);
            }

            return false;
        }
    }
}