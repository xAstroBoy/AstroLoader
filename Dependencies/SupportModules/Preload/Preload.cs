using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System;

namespace MelonLoader.Support
{
    internal static class Preload
    {
        private static void Initialize()
        {
            if (Environment.Version >= new Version("3.0.0.0"))
                return;

            string managedFolder = string.Copy(GetManagedDirectory());

            WriteResource("System.dll", Path.Combine(managedFolder, "System.dll"));
            WriteResource("System.Core.dll", Path.Combine(managedFolder, "System.Core.dll"));
            WriteResource("System.Drawing.dll", Path.Combine(managedFolder, "System.Drawing.dll"));
        }

        [MethodImpl(MethodImplOptions.InternalCall)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        private static extern string GetManagedDirectory();

        private static void WriteResource(string resourceName, string destination)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                        return;

                    byte[] data = new byte[stream.Length];
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        int read = stream.Read(data, offset, data.Length - offset);
                        if (read <= 0)
                            break;
                        offset += read;
                    }

                    if (File.Exists(destination))
                        File.Delete(destination);
                    File.WriteAllBytes(destination, data);
                }
            }
            catch { }
        }
    }
}