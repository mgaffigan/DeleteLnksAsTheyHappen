#define DEBUG
#define TRACE

using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeleteLnksAsTheyHappen
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                var syncCtx = new WindowsFormsSynchronizationContext();

                using (var listener = new LinkFileDeleter())
                using (var desktopListener = new KnownFolderWatcher(syncCtx))
                {
                    desktopListener.KeyChanged += (_1, _2) =>
                    {
                        // Superstitious 
                        Thread.Sleep(100);

                        listener.UpdateListenPath();
                    };

                    Application.Run();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                Environment.Exit(-1);
            }
        }
    }

    internal class LinkFileDeleter : IDisposable
    {
        private readonly FileSystemWatcher FSW;
        private bool isDisposed;

        public LinkFileDeleter()
        {
            this.FSW = new FileSystemWatcher(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            this.FSW.Created += this.FSW_Created;
            this.FSW.Changed += this.FSW_Created;
            this.FSW.EnableRaisingEvents = true;
            ClearInitial();
        }

        internal void UpdateListenPath()
        {
            if (isDisposed)
            {
                return;
            }

            this.FSW.EnableRaisingEvents = false;
            try
            {
                var newPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (this.FSW.Path == newPath)
                {
                    return;
                }

                Debug.WriteLine($"Desktop path changed to '{newPath}'");
                this.FSW.Path = newPath;
                ClearInitial();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not retrieve desktop dir path {ex.ToString()}");
            }
            finally
            {
                FSW.EnableRaisingEvents = true;
            }
        }

        private void FSW_Created(object sender, FileSystemEventArgs e)
        {
            if (isDisposed)
            {
                return;
            }
            if (!IsLinkFile(e.FullPath))
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_1 => DeleteFile(e.FullPath), null);
        }

        private void ClearInitial()
        {
            var dirPath = FSW.Path;
            Debug.WriteLine($"Clearing dir '{dirPath}'");
            foreach (var filePath in Directory.EnumerateFiles(dirPath).Where(IsLinkFile))
            {
                DeleteFile(filePath);
            }
        }

        private void DeleteFile(string path)
        {
            if (!IsLinkFile(path))
            {
                return;
            }
            Debug.WriteLine($"Attempting to delete '{path}'");

            try
            {
                const int TRY_COUNT = 10;
                for (int i = 0; i < TRY_COUNT; i++)
                {
                    Thread.Sleep(100 + 100 * i);

                    if (isDisposed)
                    {
                        return;
                    }

                    try
                    {
                        File.Delete(path);
                        Debug.WriteLine($"Deleted '{path}'");
                    }
                    catch when (i < TRY_COUNT - 1)
                    {
                        // no-op
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to delete '{path}' due to {ex.ToString()}");
            }
        }

        private static bool IsLinkFile(string path)
        {
            return path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".appref-ms", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            isDisposed = true;
            this.FSW.Dispose();
        }
    }

    internal class KnownFolderWatcher : IDisposable
    {
        private readonly SynchronizationContext SyncCtx;
        private readonly RegistryKey Key;
        private readonly Thread thRead;
        private readonly AutoResetEvent mreTriggered;

        public event EventHandler KeyChanged;

        private Exception Exception;
        private bool isDisposed;


        public KnownFolderWatcher(SynchronizationContext syncCtx)
        {
            this.SyncCtx = syncCtx ?? throw new ArgumentNullException(nameof(syncCtx));

            this.Key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", false)
                ?? throw new InvalidOperationException("Could not open User Shell Folders key");

            this.mreTriggered = new AutoResetEvent(false);

            this.thRead = new Thread(thRead_Main);
            this.thRead.Name = "Registry Change Reader";
            this.thRead.IsBackground = true;
            this.thRead.Start();
        }

        private void thRead_Main()
        {
            try
            {
                while (true)
                {
                    NativeMethods.RegNotifyChangeKeyValue(Key.Handle, false, 4 /* REG_NOTIFY_CHANGE_LAST_SET */, mreTriggered.SafeWaitHandle, true);
                    mreTriggered.WaitOne();
                    if (isDisposed)
                    {
                        break;
                    }

                    SyncCtx.Post(_1 =>
                    {
                        KeyChanged?.Invoke(this, EventArgs.Empty);
                    }, null);
                }
            }
            catch (Exception ex)
            {
                this.Exception = ex;
            }
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                throw new ObjectDisposedException(nameof(KnownFolderWatcher));
            }
            isDisposed = true;

            mreTriggered.Set();
            thRead.Join();

            if (this.Exception != null)
            {
                throw new InvalidOperationException("Exception from read thread", Exception);
            }
        }
    }

    internal static class NativeMethods
    {
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern uint RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubTree, uint notifyFilter, SafeWaitHandle regEvent, bool async);
    }
}
