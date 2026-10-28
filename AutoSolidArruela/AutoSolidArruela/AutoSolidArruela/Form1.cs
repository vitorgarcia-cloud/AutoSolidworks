using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AutoSolidArruela
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        // Evento gerado pelo duplo clique no formulário
        private void Form1_Load(object sender, EventArgs e)
        {
        }

        // Evento extra gerado pelo botão duplicado no designer
        private void button1_Click_1(object sender, EventArgs e)
        {
            button1_Click(sender, e);
        }

        public class WasherParameters
        {
            public string Name { get; set; }
            public double OuterDiameter { get; set; } // mm
            public double InnerDiameter { get; set; } // mm
            public double Thickness { get; set; }     // mm
        }

        private void button1_Click(object sender, EventArgs e)
        {
            string baseTemplatePath = @"C:\SW_Master\Fixadores\ARRUELAS\LISA\ARRUELA_STD.SLDPRT";
            string outputFolder = @"C:\SW_Master\Fixadores\ARRUELAS\LISA\";
            string defaultJson = @"C:\SW_Master\Fixadores\ARRUELAS\LISA\arruela.json";
            string jsonPath = File.Exists(defaultJson) ? defaultJson : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "arruela.json");

            if (!File.Exists(baseTemplatePath))
            {
                MessageBox.Show($"Base template not found:\n{baseTemplatePath}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!Directory.Exists(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
            }

            // Lê o arquivo JSON
            string jsonContent = File.ReadAllText(jsonPath);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            List<WasherParameters> washersList = serializer.Deserialize<List<WasherParameters>>(jsonContent);

            // Conecta ao SOLIDWORKS
            SldWorks swApp = GetSolidWorks();
            if (swApp == null)
            {
                MessageBox.Show("Could not connect to SOLIDWORKS.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            foreach (var washer in washersList)
            {
                int openErrors = 0;
                int openWarnings = 0;

                // 1. Abre o modelo base
                ModelDoc2 swModel = swApp.OpenDoc6(
                    baseTemplatePath,
                    (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    "",
                    ref openErrors,
                    ref openWarnings
                );

                if (swModel == null)
                {
                    MessageBox.Show($"Failed to open base model. Error code: {openErrors}");
                    continue;
                }

                // 2. Atualiza as cotas nomeadas (mm para metros)
                SetDimension(swModel, "D_Ext@Sketch1", washer.OuterDiameter * 0.001);
                SetDimension(swModel, "D_Int@Sketch1", washer.InnerDiameter * 0.001);
                SetDimension(swModel, "Thickness@Boss-Extrude1", washer.Thickness * 0.001);

                // 3. Força reconstrução da peça (Ctrl+Q via API)
                swModel.ForceRebuild3(false);

                // 4. Salva o novo arquivo
                string newFilePath = Path.Combine(outputFolder, $"{washer.Name}.SLDPRT");
                int saveErrors = 0;
                int saveWarnings = 0;

                swModel.Extension.SaveAs(
                    newFilePath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null,
                    ref saveErrors,
                    ref saveWarnings
                );

                // 5. Fecha a peça na memória
                swApp.CloseDoc(swModel.GetTitle());
            }

            MessageBox.Show("All parts have been generated successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void SetDimension(ModelDoc2 swModel, string dimensionName, double valueInMeters)
        {
            Dimension dim = (Dimension)swModel.Parameter(dimensionName);
            if (dim != null)
            {
                dim.SetSystemValue3(valueInMeters, (int)swSetValueInConfiguration_e.swSetValue_InAllConfigurations, null);
            }
            else
            {
                MessageBox.Show($"Warning: Dimension '{dimensionName}' was not found in the model!");
            }
        }

        private static SldWorks GetSolidWorks()
        {
            try
            {
                return (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            }
            catch (COMException)
            {
                Type swType = Type.GetTypeFromProgID("SldWorks.Application");
                SldWorks app = (SldWorks)Activator.CreateInstance(swType);
                app.Visible = true;
                return app;
            }
        }
    }
}