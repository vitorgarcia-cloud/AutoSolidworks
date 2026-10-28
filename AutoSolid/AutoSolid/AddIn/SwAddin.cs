using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using AutoSolid.Core;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace AutoSolid.AddIn
{
    [ComVisible(true)]
    [Guid("89B64123-5640-4718-8C23-676EFF5B66F3")]
    [DisplayName("BiblioteCad")]
    [Description("Gerador Inteligente de Bibliotecas Paramétricas CAD 3D")]
    public class SwAddin : ISwAddin
    {
        public const string ADDIN_GUID = "89B64123-5640-4718-8C23-676EFF5B66F3";
        public const string ADDIN_TITLE = "BiblioteCad";
        public const string ADDIN_DESCRIPTION = "Automação paramétrica de bibliotecas CAD 3D com IA multimodal";

        private SldWorks _swApp;
        private int _addinCookie;
        private TaskpaneView _taskpaneView;
        private TaskpaneHostControl _taskpaneHost;

        #region ISwAddin Implementation

        public bool ConnectToSW(object ThisSW, int cookie)
        {
            _swApp = (SldWorks)ThisSW;
            _addinCookie = cookie;

            // Define o callback do Add-In
            bool callbackOk = _swApp.SetAddinCallbackInfo2(0, this, _addinCookie);

            // Cria o Taskpane do SolidWorks com a logo oficial
            CreateTaskpane();

            return true;
        }

        public bool DisconnectFromSW()
        {
            DestroyTaskpane();

            _swApp = null;
            ComReleaseHelper.CollectGarbage();
            return true;
        }

        #endregion

        #region Taskpane Management

        private static string GetTaskpaneBitmapPath()
        {
            try
            {
                string dllDir = Path.GetDirectoryName(typeof(SwAddin).Assembly.Location);
                if (!string.IsNullOrEmpty(dllDir))
                {
                    string candidate = Path.Combine(dllDir, "Assets", "BiblioteCadTaskpane20.bmp");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private void CreateTaskpane()
        {
            try
            {
                string iconPath = GetTaskpaneBitmapPath();

                _taskpaneView = _swApp.CreateTaskpaneView2(iconPath, ADDIN_TITLE);
                if (_taskpaneView != null)
                {
                    _taskpaneHost = (TaskpaneHostControl)_taskpaneView.AddControl(TaskpaneHostControl.PROG_ID, string.Empty);
                    if (_taskpaneHost != null)
                    {
                        _taskpaneHost.InitializeAddInContext(_swApp);
                    }
                }
            }
            catch (Exception ex)
            {
                _swApp?.SendMsgToUser2("Falha ao inicializar Taskpane do BiblioteCad: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbWarning, (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        private void DestroyTaskpane()
        {
            try
            {
                if (_taskpaneView != null)
                {
                    _taskpaneView.DeleteView();
                    object objTaskpane = _taskpaneView;
                    ComReleaseHelper.Release(ref objTaskpane);
                    _taskpaneView = null;
                }

                _taskpaneHost = null;
            }
            catch
            {
                // Evita crash no fechamento do SolidWorks
            }
        }

        #endregion

        #region COM Registration Functions (SolidWorks AddIn Registry)

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            try
            {
                string keyPath = $@"SOFTWARE\SolidWorks\Addins\{{{ADDIN_GUID}}}";
                string iconPath = GetTaskpaneBitmapPath();

                using (RegistryKey rk = Registry.LocalMachine.CreateSubKey(keyPath))
                {
                    if (rk != null)
                    {
                        rk.SetValue(null, 0);
                        rk.SetValue("Description", ADDIN_DESCRIPTION);
                        rk.SetValue("Title", ADDIN_TITLE);
                        if (!string.IsNullOrEmpty(iconPath))
                        {
                            rk.SetValue("Icon Path", iconPath);
                        }
                    }
                }

                string loadKeyPath = $@"Software\SolidWorks\AddInsStartup\{{{ADDIN_GUID}}}";
                using (RegistryKey rk = Registry.CurrentUser.CreateSubKey(loadKeyPath))
                {
                    if (rk != null)
                    {
                        rk.SetValue(null, 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Erro ao registrar Add-In COM: " + ex.Message);
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            try
            {
                string keyPath = $@"SOFTWARE\SolidWorks\Addins\{{{ADDIN_GUID}}}";
                Registry.LocalMachine.DeleteSubKeyTree(keyPath, false);

                string loadKeyPath = $@"Software\SolidWorks\AddInsStartup\{{{ADDIN_GUID}}}";
                Registry.CurrentUser.DeleteSubKey(loadKeyPath, false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Erro ao desregistrar Add-In COM: " + ex.Message);
            }
        }

        #endregion
    }
}
