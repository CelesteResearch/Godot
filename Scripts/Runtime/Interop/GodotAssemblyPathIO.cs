using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace Celeste.Godot.Runtime.Interop
{
    internal static class GodotAssemblyPathIO
    {
        internal static string GetPhysicalPath(Assembly owner)
        {
            string? path = owner.Location;
            if (string.IsNullOrEmpty(path))
            {
                AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(owner) ??
                                              throw new InvalidOperationException(
                                                  "The actual owner has no assembly load context.");
                // Godot 4.5.1 loads project dependencies from streams. Use the same
                // dependency resolver that selected the bytes, rather than guessing a build directory.
                if (context.GetType().FullName != "GodotPlugins.PluginLoadContext")
                {
                    throw new NotSupportedException("The actual Godot project load context is required.");
                }

                var resolver = context.GetType().GetField("_resolver", BindingFlags.Instance | BindingFlags.NonPublic)
                                   ?.GetValue(context) as AssemblyDependencyResolver ??
                               throw new MissingFieldException(context.GetType().FullName, "_resolver");
                path = resolver.ResolveAssemblyToPath(owner.GetName());
            }

            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("The actual loaded owner has no deployed managed DLL.", path);
            }

            path = Path.GetFullPath(path);
            using FileStream stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            MetadataReader metadata = pe.GetMetadataReader();
            Guid moduleId = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            if (AssemblyName.GetAssemblyName(path).FullName != owner.FullName ||
                moduleId != owner.ManifestModule.ModuleVersionId)
            {
                throw new InvalidOperationException("The deployed owner DLL differs from the loaded assembly.");
            }

            return path;
        }
    }
}
