using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using AutoMaterial.Views;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace AutoMaterial
{
    /// <summary>
    /// Ponto de entrada COM do Add-In nativo para SolidWorks.
    /// Registra a aba "Ferramentas IA" e o botão "Criar Material" no CommandManager (Ribbon).
    /// </summary>
    [ComVisible(true)]
    [Guid(AddinGuid)]
    [ProgId("AutoMaterial.SwAddin")]
    public class SwAddin : ISwAddin
    {
        public const string AddinGuid = "D4A97E58-8C3B-4E91-9A2F-5D12E8C74B01";
        private const string AddinTitle = "AutoMaterial - Criador de Materiais com IA";
        private const string AddinDescription = "Criador e Curador Autônomo de Materiais Customizados (.sldmat) via LLMs";

        private const int MainCmdGroupId = 1001;
        private const int CreateMaterialCmdId = 0;
        private const string RibbonTabName = "Ferramentas IA";

        private ISldWorks _swApp;
        private ICommandManager _cmdMgr;
        private int _addinCookie;

        #region ISwAddin Implementation

        public bool ConnectToSW(object ThisSW, int cookie)
        {
            _swApp = (ISldWorks)ThisSW;
            _addinCookie = cookie;

            // Define o callback do Add-In para o SolidWorks
            _swApp.SetAddinCallbackInfo2(0, this, _addinCookie);

            // Inicializa o CommandManager (Ribbon)
            _cmdMgr = _swApp.GetCommandManager(_addinCookie);
            RegisterCommandManagerRibbon();

            return true;
        }

        public bool DisconnectFromSW()
        {
            try
            {
                if (_cmdMgr != null)
                {
                    _cmdMgr.RemoveCommandGroup(MainCmdGroupId);
                    ReleaseComObject(_cmdMgr);
                    _cmdMgr = null;
                }

                if (_swApp != null)
                {
                    ReleaseComObject(_swApp);
                    _swApp = null;
                }
            }
            catch
            {
                // Garante desconexão segura mesmo se o SolidWorks já estiver encerrando
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        #endregion

        #region CommandManager (Ribbon UI)

        private void RegisterCommandManagerRibbon()
        {
            int errors = 0;

            // Cria o CommandGroup "Ferramentas IA"
            ICommandGroup cmdGroup = _cmdMgr.CreateCommandGroup2(
                MainCmdGroupId,
                RibbonTabName,
                AddinTitle,
                AddinDescription,
                -1,
                true,
                ref errors);

            // Associa os ícones oficiais do AutoMaterial ao CommandGroup e Ribbon
            try
            {
                string dllDir = Path.GetDirectoryName(typeof(SwAddin).Assembly.Location);
                string icon20 = Path.Combine(dllDir, "Assets", "AutoMaterial20.bmp");
                string icon32 = Path.Combine(dllDir, "Assets", "AutoMaterial32.bmp");
                string icon40 = Path.Combine(dllDir, "Assets", "AutoMaterial40.bmp");

                if (File.Exists(icon20) && File.Exists(icon32))
                {
                    cmdGroup.SmallIconList = icon20;
                    cmdGroup.LargeIconList = icon32;
                    cmdGroup.SmallMainIcon = icon20;
                    cmdGroup.LargeMainIcon = File.Exists(icon40) ? icon40 : icon32;
                }
            }
            catch { }

            int menuToolbarOption = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);

            // Adiciona o comando "Criar Material" vinculado ao callback OnCreateMaterialClick
            int cmdIndex = cmdGroup.AddCommandItem2(
                "Criar Material",
                -1,
                "Cria e aplica um material customizado (.sldmat) usando Inteligência Artificial",
                "Criar Material",
                0,
                nameof(OnCreateMaterialClick),
                nameof(EnableCreateMaterial),
                CreateMaterialCmdId,
                menuToolbarOption);

            cmdGroup.HasToolbar = true;
            cmdGroup.HasMenu = true;
            cmdGroup.Activate();

            // Registra a aba "Ferramentas IA" no Ribbon para documentos de Peça (Part) e Montagem
            int[] targetDocTypes = new[]
            {
                (int)swDocumentTypes_e.swDocPART,
                (int)swDocumentTypes_e.swDocASSEMBLY
            };

            foreach (int docType in targetDocTypes)
            {
                CommandTab cmdTab = _cmdMgr.GetCommandTab(docType, RibbonTabName);

                // Se a aba já existir de uma versão anterior, recria para atualizar os botões
                if (cmdTab != null)
                {
                    _cmdMgr.RemoveCommandTab(cmdTab);
                    ReleaseComObject(cmdTab);
                    cmdTab = null;
                }

                cmdTab = _cmdMgr.AddCommandTab(docType, RibbonTabName);
                if (cmdTab != null)
                {
                    CommandTabBox cmdBox = cmdTab.AddCommandTabBox();

                    int[] cmdIDs = new int[] { cmdGroup.get_CommandID(cmdIndex) };
                    int[] textDisplayStyles = new int[]
                    {
                        (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow
                    };

                    cmdBox.AddCommands(cmdIDs, textDisplayStyles);

                    ReleaseComObject(cmdBox);
                    ReleaseComObject(cmdTab);
                }
            }

            ReleaseComObject(cmdGroup);
        }

        /// <summary>
        /// Callback disparado pelo SolidWorks quando o usuário clica no botão "Criar Material".
        /// Instancia e abre a janela WPF como modal (ShowDialog), bloqueando a UI do SolidWorks.
        /// </summary>
        public void OnCreateMaterialClick()
        {
            IFrame swFrame = null;
            try
            {
                var modalWindow = new MaterialGeneratorWindow(_swApp);

                // Vincula o HWND da janela principal do SolidWorks como Owner da janela WPF
                swFrame = _swApp.IFrameObject();
                if (swFrame != null)
                {
                    IntPtr swHwnd = new IntPtr(swFrame.GetHWnd());
                    var interopHelper = new WindowInteropHelper(modalWindow)
                    {
                        Owner = swHwnd
                    };
                }

                // Abre de forma modal bloqueando a interface do SolidWorks por segurança
                modalWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                _swApp?.SendMsgToUser2(
                    $"Erro ao abrir o Criador de Materiais IA:\n{ex.Message}",
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            finally
            {
                ReleaseComObject(swFrame);
            }
        }

        /// <summary>
        /// Método de controle de estado do botão no Ribbon do SolidWorks.
        /// Retorna 1 (habilitado) quando há uma peça ativa.
        /// </summary>
        public int EnableCreateMaterial()
        {
            IModelDoc2 activeDoc = null;
            try
            {
                activeDoc = _swApp?.ActiveDoc as IModelDoc2;
                if (activeDoc != null && activeDoc.GetType() == (int)swDocumentTypes_e.swDocPART)
                {
                    return 1; // Habilitado
                }

                // Mantém habilitado para exibir aviso amigável caso o usuário clique fora de uma peça
                return 1;
            }
            finally
            {
                ReleaseComObject(activeDoc);
            }
        }

        #endregion

        #region COM Registration (RegAsm)

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            try
            {
                string keyPath = $@"SOFTWARE\SolidWorks\AddIns\{{{t.GUID}}}";
                using (RegistryKey rk = Registry.LocalMachine.CreateSubKey(keyPath))
                {
                    rk?.SetValue(null, 0);
                    rk?.SetValue("Title", AddinTitle);
                    rk?.SetValue("Description", AddinDescription);

                    string dllDir = Path.GetDirectoryName(t.Assembly.Location);
                    string iconPath = Path.Combine(dllDir ?? "", "Assets", "AutoMaterial20.bmp");
                    if (File.Exists(iconPath))
                    {
                        rk?.SetValue("Icon Path", iconPath);
                    }
                }

                string startupKeyPath = $@"Software\SolidWorks\AddInsStartup\{{{t.GUID}}}";
                using (RegistryKey rkStartup = Registry.CurrentUser.CreateSubKey(startupKeyPath))
                {
                    rkStartup?.SetValue(null, 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Erro ao registrar o Add-In no Registro do Windows: {ex.Message}");
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            try
            {
                string keyPath = $@"SOFTWARE\SolidWorks\AddIns\{{{t.GUID}}}";
                Registry.LocalMachine.DeleteSubKeyTree(keyPath, false);

                string startupKeyPath = $@"Software\SolidWorks\AddInsStartup\{{{t.GUID}}}";
                Registry.CurrentUser.DeleteSubKeyTree(startupKeyPath, false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Erro ao remover o Add-In do Registro do Windows: {ex.Message}");
            }
        }

        #endregion

        private static void ReleaseComObject(object comObj)
        {
            if (comObj != null && Marshal.IsComObject(comObj))
            {
                try
                {
                    Marshal.ReleaseComObject(comObj);
                }
                catch
                {
                    // Ignora caso já liberado
                }
            }
        }
    }
}
