using System.IO;
using System.Collections.Generic;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OSBuilder
{
    public class ProjectSource
    {
        // Supported types are
        // Directory (Path, Target)
        // File (Path, Target)
        // Chef (Package, Channel, Platform, Arch, Target)
        public string Type { get; set; }

        // Shared properties
        public string Path { get; set; }
        public string Target { get; set; }

        // Chef specific properties
        public string Package { get; set; }
        public string Channel { get; set; }
        public string Platform { get; set; }
        public string Arch { get; set; }
    }

    public class ProjectPartition
    {
        public string Label { get; set; }
        public string Type { get; set; }
        public string Guid { get; set; }
        public string Size { get; set; }
        public List<string> Attributes { get; set; }
        public string VbrImage { get; set; }
        public string ReservedSectorsImage { get; set; }
        public List<ProjectSource> Sources { get; set; }
    }

    public class ProjectConfiguration
    {
        public string Scheme { get; set; }
        public string Size { get; set; }
        public List<ProjectPartition> Partitions { get; set; }

        public static ProjectConfiguration Parse(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            
            var data = File.ReadAllText(path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(HyphenatedNamingConvention.Instance)
                .Build();

            var projectConfiguration = deserializer.Deserialize<ProjectConfiguration>(data);
            return projectConfiguration;
        }
    }
}
