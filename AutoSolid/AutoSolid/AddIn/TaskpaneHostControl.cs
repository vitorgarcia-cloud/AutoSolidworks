using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using AutoSolid.Services;
using AutoSolid.ViewModels;
using AutoSolid.Views;
using SolidWorks.Interop.sldworks;

namespace AutoSolid.AddIn
{
    [ComVisible(true)]
    [ProgId(PROG_ID)]
    [Guid("713629BA-C241-4B53-B25E-08A86B828C0F")]
    public class TaskpaneHostControl : UserControl
    {
        public const string PROG_ID = "AutoSolid.TaskpaneHostControl";

        private ElementHost _elementHost;
        private MainTaskpaneView _wpfView;
        private MainViewModel _viewModel;

        public TaskpaneHostControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            _elementHost = new ElementHost
            {
                Dock = DockStyle.Fill
            };

            _wpfView = new MainTaskpaneView();
            _elementHost.Child = _wpfView;

            Controls.Add(_elementHost);
        }

        public void InitializeAddInContext(SldWorks swApp)
        {
            if (swApp == null) return;

            var swService = new SwAutomationService(swApp);
            _viewModel = new MainViewModel(swService);
            _wpfView.DataContext = _viewModel;
        }

        public MainViewModel ViewModel => _viewModel;
    }
}
