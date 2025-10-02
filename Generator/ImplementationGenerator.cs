using System;
using System.Collections.Generic;
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

            // Coleta os using statements da interface
            var usingStatements = GetUsingStatementsFromInterface(iface);
            if (usingStatements.Any())
            {
                foreach (var usingStatement in usingStatements)
                {
                    sb.AppendLine(usingStatement);
                }
                sb.AppendLine();
            }

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

                // Gera propriedades com required init (exceto nullable types)
                foreach (var p in positionalProps)
                {
                    var type = GetTypeDisplayString(p.Type, usingStatements);
                    var name = p.Name;
                    var isNullable = IsNullableType(p.Type);

                    // Usa 'required' apenas para tipos não-nullable
                    var requiredKeyword = isNullable ? "" : "required ";
                    sb.AppendLine($"        public {requiredKeyword}{type} {name} {{ get; init; }}");
                }

                if (positionalProps.Length > 0)
                    sb.AppendLine();

                // Indexers
                AppendIndexers(sb, indexers, usingStatements);

                // Métodos
                foreach (var method in methods)
                {
                    AppendMethodStub(sb, method, usingStatements);
                    sb.AppendLine();
                }

                // Eventos
                foreach (var ev in events)
                {
                    var eventType = GetTypeDisplayString(ev.Type, usingStatements);
                    sb.AppendLine($"        public event {eventType} {ev.Name};");
                }

                sb.AppendLine("    }");
            }
            else
            {
                // CLASS com propriedades init
                sb.AppendLine($"    public partial class {typeName} : {iface.ToDisplayString()}");
                sb.AppendLine("    {");

                // Propriedades com required (exceto nullable types)
                foreach (var p in positionalProps)
                {
                    var type = GetTypeDisplayString(p.Type, usingStatements);
                    var name = p.Name;
                    var isNullable = IsNullableType(p.Type);

                    // Usa 'required' apenas para tipos não-nullable
                    var requiredKeyword = isNullable ? "" : "required ";
                    sb.AppendLine($"        public {requiredKeyword}{type} {name} {{ get; init; }}");
                }

                sb.AppendLine();

                // Indexers
                AppendIndexers(sb, indexers, usingStatements);

                // Métodos
                foreach (var method in methods)
                {
                    AppendMethodStub(sb, method, usingStatements);
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

        private static void AppendIndexers(StringBuilder sb, IPropertySymbol[] indexers, IEnumerable<string> usingStatements)
        {
            foreach (var idx in indexers)
            {
                var indexParams = string.Join(", ", idx.Parameters.Select(p => $"{GetTypeDisplayString(p.Type, usingStatements)} {p.Name}"));
                var type = GetTypeDisplayString(idx.Type, usingStatements);
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

        private static void AppendMethodStub(StringBuilder sb, IMethodSymbol method, IEnumerable<string> usingStatements)
        {
            var returnType = GetTypeDisplayString(method.ReturnType, usingStatements);
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
                return $"{modifiers}{GetTypeDisplayString(p.Type, usingStatements)} {p.Name}{defaultValue}";
            }));

            sb.AppendLine($"        public {returnType} {method.Name}({parameters})");
            sb.AppendLine("        {");
            sb.AppendLine("            throw new System.NotImplementedException();");
            sb.AppendLine("        }");
        }

        private static IEnumerable<string> GetUsingStatementsFromInterface(INamedTypeSymbol iface)
        {
            var usings = new HashSet<string>();

            // Percorre todas as localizações da interface para coletar os usings
            foreach (var location in iface.Locations)
            {
                if (location.SourceTree != null)
                {
                    var root = location.SourceTree.GetRoot();
                    var usingDirectives = root.DescendantNodes()
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>()
                        .Where(u => !u.StaticKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)) // Exclui using static
                        .Select(u => u.ToString().Trim())
                        .Where(u => !string.IsNullOrEmpty(u));

                    foreach (var usingDir in usingDirectives)
                    {
                        usings.Add(usingDir);
                    }
                }
            }

            // Coleta namespaces dos tipos usados nas propriedades, métodos e eventos
            var props = iface.GetMembers().OfType<IPropertySymbol>().ToArray();
            var methods = iface.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).ToArray();
            var events = iface.GetMembers().OfType<IEventSymbol>().ToArray();

            // Adiciona using statements dos tipos das propriedades
            foreach (var prop in props)
            {
                AddNamespacesFromType(prop.Type, usings);
            }

            // Adiciona using statements dos tipos dos métodos
            foreach (var method in methods)
            {
                AddNamespacesFromType(method.ReturnType, usings);
                foreach (var param in method.Parameters)
                {
                    AddNamespacesFromType(param.Type, usings);
                }
            }

            // Adiciona using statements dos tipos dos eventos
            foreach (var evt in events)
            {
                AddNamespacesFromType(evt.Type, usings);
            }

            return usings.OrderBy(u => u);
        }

        private static void AddNamespacesFromType(ITypeSymbol type, HashSet<string> usings)
        {
            if (type == null) return;

            // Adiciona o namespace do tipo principal
            var ns = type.ContainingNamespace?.ToDisplayString();
            if (!string.IsNullOrEmpty(ns) && ns != "System" && !ns!.StartsWith("System."))
            {
                usings.Add($"using {ns};");
            }

            // Para tipos genéricos, adiciona os namespaces dos argumentos de tipo
            if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                foreach (var typeArg in namedType.TypeArguments)
                {
                    AddNamespacesFromType(typeArg, usings);
                }
            }

            // Para arrays, adiciona o namespace do tipo de elemento
            if (type is IArrayTypeSymbol arrayType)
            {
                AddNamespacesFromType(arrayType.ElementType, usings);
            }
        }

        private static string GetTypeDisplayString(ITypeSymbol type, IEnumerable<string> usingStatements)
        {
            // Usa FullyQualifiedFormat para garantir que todos os tipos sejam resolvidos corretamente
            var fullTypeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            // Remove o prefixo "global::" se presente
            if (fullTypeName.StartsWith("global::"))
            {
                fullTypeName = fullTypeName.Substring(8);
            }

            // Para tipos simples do System, usa o nome simples
            if (fullTypeName.StartsWith("System."))
            {
                var simpleName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                if (IsSystemType(simpleName))
                {
                    return simpleName;
                }
            }

            return fullTypeName;
        }

        private static bool IsSystemType(string typeName)
        {
            // Lista de tipos comuns do System que podem usar nome simples
            var systemTypes = new HashSet<string>
            {
                "string", "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
                "float", "double", "decimal", "bool", "char", "object", "DateTime", "Guid",
                "TimeSpan", "void"
            };

            return systemTypes.Contains(typeName.Replace("?", ""));
        }

        private static bool IsNullableType(ITypeSymbol type)
        {
            // Verifica se é nullable reference type (string?, object?, etc.)
            if (type.CanBeReferencedByName && type.NullableAnnotation == NullableAnnotation.Annotated)
                return true;

            // Verifica se é nullable value type (int?, DateTime?, etc.)
            if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return true;

            return false;
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

