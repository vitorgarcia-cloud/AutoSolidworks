using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AutoSolidParaf
{
    public partial class Form1 : Form
    {
        string caminhoTemplate = @"C:\Users\playp\Desktop\VG_projetos\SOLIDWORKS\TESTE\Parafuso_Base.SLDPRT";
        string pastaDestino = @"C:\Users\playp\Desktop\VG_projetos\SOLIDWORKS\TESTE\PARAFUSO-SEXTAVADO\";
        string localJson = @"C:\Users\playp\Desktop\VG_projetos\SOLIDWORKS\TESTE\Parametros.json";

        public Form1()
        {
            InitializeComponent();
        }
        private void Form1_Load(object sender, EventArgs e)
        {
        }
        public class Parametros
        {
            public string Nome { get; set; }
            public double Diametro { get; set; }
            public double ComprimentoUtil { get; set; }
            public double TamanhoCabeca { get; set; }
            public double AlturaCabeca { get; set; }
        }
        private void button1_Click_1(object sender, EventArgs e)
        {
            // Lê o arquivo JSON e transforma em uma lista de objetos Parametros

            if (!File.Exists(localJson))
            {
                MessageBox.Show("Não foi possível encontrar o arquivo (Parametros.json) no caminho especificado:\n" + localJson, "Arquivo não encontrado", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string parametrosJson = File.ReadAllText(localJson);        
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            List<Parametros> parametros = serializer.Deserialize<List<Parametros>>(parametrosJson);

            // Conecta ao SolidWorks
            SldWorks swApp = ObterSolidworks();
            if (swApp == null)
            {
                MessageBox.Show("Não foi possível conectar ao SolidWorks. Certifique-se de que o SolidWorks está em execução.");
                return;
            }

            // Garante que a pasta de destino exista antes de tentar salvar
            if (!Directory.Exists(pastaDestino))
            {
                Directory.CreateDirectory(pastaDestino);
            }

            foreach (var parametro in parametros)
            {
                int erros = 0;
                int avisos = 0;

                ModelDoc2 peca = swApp.OpenDoc6(caminhoTemplate, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref erros, ref avisos);

                if (peca == null) continue; // Evita travar se a peça base não for encontrada

                MudarMedida(peca, "DiamRosca@Esboço1", parametro.Diametro * 0.001);
                MudarMedida(peca, "CompUtil@Esboço1", parametro.ComprimentoUtil * 0.001);
                MudarMedida(peca, "TamCab@Esboço1", parametro.TamanhoCabeca * 0.001);
                MudarMedida(peca, "AltCab@Esboço1", parametro.AlturaCabeca * 0.001);

                peca.ForceRebuild3(false);

                string nomeFileSalvar = pastaDestino + parametro.Nome + ".SLDPRT";
                peca.Extension.SaveAs(nomeFileSalvar, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref erros, ref avisos);

                swApp.CloseDoc(peca.GetTitle());
            }

            MessageBox.Show("Todas as peças foram geradas com sucesso!", "Sucesso");
        }
        private SldWorks ObterSolidworks()
        {
            try
            {
                return (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            }
            catch
            {
                Type swType = Type.GetTypeFromProgID("SldWorks.Application");
                SldWorks app = (SldWorks)Activator.CreateInstance(swType);
                app.Visible = true;
                return app;
            }
        }
        private void MudarMedida(ModelDoc2 peca, string nomeCota, double valorMetros)
        {
            Dimension cota = peca.Parameter(nomeCota);

            if (cota != null)
            {
                cota.SetSystemValue3(valorMetros, (int)swSetValueInConfiguration_e.swSetValue_InAllConfigurations, null);
            }
        }
    }
}