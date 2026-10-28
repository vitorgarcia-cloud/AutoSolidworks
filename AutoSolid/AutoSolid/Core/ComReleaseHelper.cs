using System;
using System.Runtime.InteropServices;

namespace AutoSolid.Core
{
    public static class ComReleaseHelper
    {
        /// <summary>
        /// Libera um objeto COM gerenciado pela API do SolidWorks com segurança.
        /// </summary>
        public static void Release(ref object comObj)
        {
            if (comObj == null) return;

            try
            {
                if (Marshal.IsComObject(comObj))
                {
                    Marshal.ReleaseComObject(comObj);
                }
            }
            catch
            {
                // Ignora falhas se o runtime COM já liberou o objeto
            }
            finally
            {
                comObj = null;
            }
        }

        /// <summary>
        /// Força a liberação final e dispara coleta de lixo determinística para objetos COM órfãos.
        /// </summary>
        public static void CollectGarbage()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
