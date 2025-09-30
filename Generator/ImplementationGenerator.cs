using System;
using System.Linq;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;


namespace AutoImplementation.CodeGeneration
{
    [Generator]
    public sealed class ImplementationGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // 1) Injeta o atributo no projeto consumidor
            context.RegisterPostInitializationOutput(pi =>
            {
                pi.AddSource("GenerateImplementationAttribute.g.cs", @"
using System;

namespace AutoImplementation.CodeGeneration
{
    /// <summary>
    /// Anote interfaces para gerar uma implementação concreta.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class GenerateImplementationAttribute : Attribute
    {
        /// <summary>
        /// Nome da classe/record a ser gerada. Se null, usa o nome da interface sem o prefixo 'I'.
        /// </summary>
        public string? ClassName { get; }

        /// <summary>
        /// Se true (default), gera um 'record' posicional; se false, gera uma 'class' com construtor.
        /// </summary>
        public bool UseRecord { get; }

        public GenerateImplementationAttribute(string? className = null, bool useRecord = true)
        {
            ClassName = className;
            UseRecord = useRecord;
        }
    }
}
");
            });

            // 2) Pipeline sintático barato: interfaces com algum atributo
            var candidates = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (s, _) => s is InterfaceDeclarationSyntax ids && ids.AttributeLists.Count > 0,
                transform: static (ctx, _) => (InterfaceDeclarationSyntax)ctx.Node
            );

            // 3) Combina com a compilação para análise semântica
            var compilationAndInterfaces = context.CompilationProvider.Combine(candidates.Collect());

            // 4) Emite o código
            context.RegisterSourceOutput(compilationAndInterfaces, Execute);
        }

        private static bool TryGetGenerateImplementationAttribute(INamedTypeSymbol iface, out AttributeData? attribute)
        {
            foreach (var a in iface.GetAttributes())
            {
                var attrClass = a.AttributeClass;
                if (attrClass is null) continue;

                var simple = attrClass.Name; // ex: "GenerateImplementationAttribute"
                var full = attrClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (simple is "GenerateImplementationAttribute" or "GenerateImplementation" ||
                    full.EndsWith(".GenerateImplementationAttribute", StringComparison.Ordinal) ||
                    full.EndsWith(".GenerateImplementation", StringComparison.Ordinal))
                {
                    attribute = a;
                    return true;
                }
            }

            attribute = null;
            return false;
        }

        private static (string TypeName, bool UseRecord) ParseOptions(INamedTypeSymbol iface, AttributeData? attribute)
        {
            string? className = null;
            bool useRecord = true;

            if (attribute is not null)
            {
                // Argumentos posicionais
                if (attribute.ConstructorArguments.Length > 0)
                {
                    if (attribute.ConstructorArguments[0].Value is string s && !string.IsNullOrWhiteSpace(s))
                        className = s;
                }
                if (attribute.ConstructorArguments.Length > 1)
                {
                    if (attribute.ConstructorArguments[1].Value is bool b)
                        useRecord = b;
                }

                // Named arguments (ex.: useRecord: false)
                foreach (var kv in attribute.NamedArguments)
                {
                    if (kv.Key is "UseRecord" && kv.Value.Value is bool b2)
                        useRecord = b2;
                    if (kv.Key is nameof(className) && kv.Value.Value is string s2 && !string.IsNullOrWhiteSpace(s2))
                        className = s2;
                }
            }

            if (string.IsNullOrWhiteSpace(className))
            {
                var name = iface.Name;
                className = name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1])
                    ? name.Substring(1)
                    : name;
            }

            return (className!, useRecord);
        }

        private static readonly DiagnosticDescriptor IndexerInfo = new(
            id: "GI0001",
            title: "Indexer em interface não é suportado na geração posicional",
            messageFormat: "Interface '{0}' possui indexer '{1}'. Ele será gerado com NotImplementedException.",
            category: "GenerateImplementation",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true
        );

        private static void Execute(SourceProductionContext context, (Compilation Compilation, ImmutableArray<InterfaceDeclarationSyntax> Interfaces) data)
        {
            var (compilation, interfaces) = data;
            if (interfaces.IsDefaultOrEmpty) return;

            foreach (var ids in interfaces.Distinct())
            {
                var semanticModel = compilation.GetSemanticModel(ids.SyntaxTree);
                if (semanticModel.GetDeclaredSymbol(ids) is not INamedTypeSymbol iface)
                    continue;

                if (iface.TypeKind != TypeKind.Interface) continue;
                if (!TryGetGenerateImplementationAttribute(iface, out var attr))
                    continue;

                var (typeName, useRecord) = ParseOptions(iface, attr);
                if (string.IsNullOrWhiteSpace(typeName))
                    continue;

                var ns = iface.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                var src = GenerateImplementationSource(context, iface, typeName, ns, useRecord);

                var safeNs = string.IsNullOrEmpty(ns) ? "" : ns + ".";
                context.AddSource($"{safeNs}{typeName}.g.cs", src);
            }
        }

        private static string GenerateImplementationSource(SourceProductionContext context, INamedTypeSymbol iface, string typeName, string namespaceName, bool useRecord)
        {
            var props = iface.GetMembers().OfType<IPropertySymbol>().ToArray();
            var methods = iface.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).ToArray();
            var events = iface.GetMembers().OfType<IEventSymbol>().ToArray();

            var indexers = props.Where(p => p.IsIndexer).ToArray();
            foreach (var idx in indexers)
            {
                context.ReportDiagnostic(Diagnostic.Create(IndexerInfo, idx.Locations.FirstOrDefault(), iface.Name, idx.Name));
            }

            var positionalProps = props.Where(p => !p.IsIndexer).ToArray();

            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine($"namespace {namespaceName}");
                sb.AppendLine("{");
            }

            sb.AppendLine("    // <auto-generated/>");
            sb.AppendLine($"    // Gerado por ImplementationGenerator em {DateTimeOffset.Now:O}");
            sb.AppendLine();

            if (useRecord)
            {
                // RECORD com propriedades init (não posicional)
                sb.AppendLine($"    public partial record {typeName} : {iface.ToDisplayString()}");
                sb.AppendLine("    {");

                // Gera propriedades com required init
                foreach (var p in positionalProps)
                {
                    var type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    var name = p.Name;

                    // Usa 'required' para garantir inicialização
                    sb.AppendLine($"        public required {type} {name} {{ get; init; }}");
                }

                if (positionalProps.Length > 0)
                    sb.AppendLine();

                // Indexers
                AppendIndexers(sb, indexers);

                // Métodos
                foreach (var method in methods)
                {
                    AppendMethodStub(sb, method);
                    sb.AppendLine();
                }

                // Eventos
                foreach (var ev in events)
                {
                    sb.AppendLine($"        public event {ev.Type.ToDisplayString()} {ev.Name};");
                }

                sb.AppendLine("    }");
            }
            else
            {
                // CLASS com propriedades init
                sb.AppendLine($"    public partial class {typeName} : {iface.ToDisplayString()}");
                sb.AppendLine("    {");

                // Propriedades com required
                foreach (var p in positionalProps)
                {
                    var type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    var name = p.Name;
                    sb.AppendLine($"        public required {type} {name} {{ get; init; }}");
                }

                sb.AppendLine();

                // Indexers
                AppendIndexers(sb, indexers);

                // Métodos
                foreach (var method in methods)
                {
                    AppendMethodStub(sb, method);
                    sb.AppendLine();
                }

                // Eventos
                foreach (var ev in events)
                {
                    sb.AppendLine($"        public event {ev.Type.ToDisplayString()} {ev.Name};");
                }

                sb.AppendLine("    }");
            }

            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine("}");
            }

            return sb.ToString();
        }
        private static string GenerateImplementationSource2(SourceProductionContext context, INamedTypeSymbol iface, string typeName, string namespaceName, bool useRecord)
        {
            var props = iface.GetMembers().OfType<IPropertySymbol>().ToArray();
            var methods = iface.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).ToArray();
            var events = iface.GetMembers().OfType<IEventSymbol>().ToArray();

            var indexers = props.Where(p => p.IsIndexer).ToArray();
            foreach (var idx in indexers)
            {
                context.ReportDiagnostic(Diagnostic.Create(IndexerInfo, idx.Locations.FirstOrDefault(), iface.Name, idx.Name));
            }

            var positionalProps = props.Where(p => !p.IsIndexer).ToArray();

            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine($"namespace {namespaceName}");
                sb.AppendLine("{");
            }

            sb.AppendLine("    // <auto-generated/>");
            sb.AppendLine($"    // Gerado por ImplementationGenerator em {DateTimeOffset.Now:O}");
            sb.AppendLine();

            if (useRecord)
            {
                // RECORD posicional
                var ctorParams = string.Join(", ",
                    positionalProps.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {ToParamName(p.Name)}"));

                sb.AppendLine($"    public partial record {typeName}({ctorParams}) : {iface.ToDisplayString()}");
                sb.AppendLine("    {");
                // Indexers
                AppendIndexers(sb, indexers);
                // Métodos
                foreach (var method in methods)
                {
                    AppendMethodStub(sb, method);
                    sb.AppendLine();
                }
                // Eventos
                foreach (var ev in events)
                {
                    sb.AppendLine($"        public event {ev.Type.ToDisplayString()} {ev.Name};");
                }
                sb.AppendLine("    }");
            }
            else
            {
                // CLASS com construtor que recebe todas as props e atribui a auto-propriedades init-only
                sb.AppendLine($"    public partial class {typeName} : {iface.ToDisplayString()}");
                sb.AppendLine("    {");
                // Propriedades
                foreach (var p in positionalProps)
                {
                    var type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    var name = p.Name;
                    // Se a interface tem get-only, geramos init; se tiver set na interface, a classe pode ter set; mas manter init; é melhor para mensagens
                    sb.AppendLine($"        public {type} {name} {{ get; init; }}");
                }
                sb.AppendLine();

                // Construtor
                var ctorParamList = string.Join(", ",
                    positionalProps.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {ToParamName(p.Name)}"));
                sb.AppendLine($"        public {typeName}({ctorParamList})");
                sb.AppendLine("        {");
                foreach (var p in positionalProps)
                {
                    var param = ToParamName(p.Name);
                    sb.AppendLine($"            {p.Name} = {param};");
                }
                sb.AppendLine("        }");
                sb.AppendLine();

                // Indexers
                AppendIndexers(sb, indexers);

                // Métodos
                foreach (var method in methods)
                {
                    AppendMethodStub(sb, method);
                    sb.AppendLine();
                }

                // Eventos
                foreach (var ev in events)
                {
                    sb.AppendLine($"        public event {ev.Type.ToDisplayString()} {ev.Name};");
                }

                sb.AppendLine("    }");
            }

            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        private static void AppendIndexers(StringBuilder sb, IPropertySymbol[] indexers)
        {
            foreach (var idx in indexers)
            {
                var indexParams = string.Join(", ", idx.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
                var type = idx.Type.ToDisplayString();
                var hasGet = idx.GetMethod is not null;
                var hasSet = idx.SetMethod is not null;

                sb.AppendLine($"        public {type} this[{indexParams}]");
                sb.AppendLine("        {");
                if (hasGet)
                    sb.AppendLine("            get => throw new System.NotImplementedException();");
                if (hasSet)
                    sb.AppendLine("            set => throw new System.NotImplementedException();");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
        }

        private static void AppendMethodStub(StringBuilder sb, IMethodSymbol method)
        {
            var returnType = method.ReturnType.ToDisplayString();
            var parameters = string.Join(", ", method.Parameters.Select(p =>
            {
                var modifiers = p.RefKind switch
                {
                    RefKind.Ref => "ref ",
                    RefKind.Out => "out ",
                    RefKind.In => "in ",
                    _ => string.Empty
                };
                var defaultValue = p.HasExplicitDefaultValue ? $" = {FormatDefaultValue(p)}" : string.Empty;
                return $"{modifiers}{p.Type.ToDisplayString()} {p.Name}{defaultValue}";
            }));

            sb.AppendLine($"        public {returnType} {method.Name}({parameters})");
            sb.AppendLine("        {");
            sb.AppendLine("            throw new System.NotImplementedException();");
            sb.AppendLine("        }");
        }

        private static string ToParamName(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return "value";
            if (propertyName.Length == 1) return propertyName.ToLowerInvariant();

            var candidate = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
            return candidate switch
            {
                "value" => "@value",
                _ => candidate
            };
        }

        private static string FormatDefaultValue(IParameterSymbol p)
        {
            if (p.ExplicitDefaultValue is null) return "null";
            return p.ExplicitDefaultValue switch
            {
                string s => "@\"" + s.Replace("\"", "\"\"") + "\"",
                char c => "'" + (c == '\'' ? "\\'" : c.ToString()) + "'",
                bool b => b ? "true" : "false",
                _ => Convert.ToString(p.ExplicitDefaultValue, System.Globalization.CultureInfo.InvariantCulture) ?? "null"
            };
        }
    }
}

