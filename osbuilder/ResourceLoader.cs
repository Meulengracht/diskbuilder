using System.IO;
using System.Reflection;
using System.Linq;

namespace OSBuilder
{
    public class ResourceLoader
    {
        public static byte[] Load(string path)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames().Single(str => str.EndsWith(path));
            
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }
    }
}
