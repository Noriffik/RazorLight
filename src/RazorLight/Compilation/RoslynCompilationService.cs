﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyModel;
using RazorLight.Generation;
using RazorLight.Internal;
using DependencyContextCompilationOptions = Microsoft.Extensions.DependencyModel.CompilationOptions;

namespace RazorLight.Compilation
{
	public sealed class RoslynCompilationService : ICompilationService
	{
		private readonly IMetadataReferenceManager metadataReferenceManager;
		private readonly bool isDevelopment;
		private readonly Assembly operatingAssembly;
		private readonly List<MetadataReference> metadataReferences = new List<MetadataReference>();

		public RoslynCompilationService(IMetadataReferenceManager referenceManager, Assembly operatingAssembly)
		{
			this.metadataReferenceManager = referenceManager ?? throw new ArgumentNullException(nameof(referenceManager));
			this.operatingAssembly = operatingAssembly ?? throw new ArgumentNullException(nameof(operatingAssembly));

			isDevelopment = IsAssemblyDebugBuild(OperatingAssembly);
			var pdbFormat = SymbolsUtility.SupportsFullPdbGeneration() ?
				DebugInformationFormat.Pdb :
				DebugInformationFormat.PortablePdb;

			EmitOptions = new EmitOptions(debugInformationFormat: pdbFormat);
		}

		#region Options

		public Assembly OperatingAssembly => operatingAssembly;
		public EmitOptions EmitOptions { get; }
		public CSharpCompilationOptions CSharpCompilationOptions
		{
			get
			{
				EnsureOptions();
				return _compilationOptions;
			}
		}
		public CSharpParseOptions ParseOptions
		{
			get
			{
				EnsureOptions();
				return _parseOptions;
			}
		}

		#endregion

		private CSharpParseOptions _parseOptions;
		private CSharpCompilationOptions _compilationOptions;

		private static readonly object locker = new object();

		private bool _optionsInitialized;
		private void EnsureOptions()
		{
			lock(locker)
			{
				if (_optionsInitialized) return;
				var dependencyContextOptions = GetDependencyContextCompilationOptions();
				_parseOptions = GetParseOptions(dependencyContextOptions);
				_compilationOptions = GetCompilationOptions(dependencyContextOptions);

				metadataReferences.AddRange(metadataReferenceManager.Resolve(OperatingAssembly));

				_optionsInitialized = true;
			}
		}


		public Assembly CompileAndEmit(IGeneratedRazorTemplate razorTemplate)
		{
			if (razorTemplate == null)
			{
				throw new ArgumentNullException(nameof(razorTemplate));
			}

			string assemblyName = Path.GetRandomFileName();
			var compilation = CreateCompilation(razorTemplate.GeneratedCode, assemblyName);

			using (var assemblyStream = new MemoryStream())
			using (var pdbStream = new MemoryStream())
			{
				var result = compilation.Emit(
					assemblyStream,
					pdbStream,
					options: EmitOptions);

				if (!result.Success)
				{
					List<Diagnostic> errorsDiagnostics = result.Diagnostics
							.Where(d => d.IsWarningAsError || d.Severity == DiagnosticSeverity.Error)
							.ToList();

					StringBuilder builder = new StringBuilder();
					builder.AppendLine("Failed to compile generated Razor template:");

					var errorMessages = new List<string>();
					foreach (Diagnostic diagnostic in errorsDiagnostics)
					{
						FileLinePositionSpan lineSpan = diagnostic.Location.SourceTree.GetMappedLineSpan(diagnostic.Location.SourceSpan);
						string errorMessage = diagnostic.GetMessage();
						string formattedMessage = $"- ({lineSpan.StartLinePosition.Line}:{lineSpan.StartLinePosition.Character}) {errorMessage}";

						errorMessages.Add(formattedMessage);
						builder.AppendLine(formattedMessage);
					}

					builder.AppendLine("\nSee CompilationErrors for detailed information");

					throw new TemplateCompilationException(builder.ToString(), errorMessages);
				}

				assemblyStream.Seek(0, SeekOrigin.Begin);
				pdbStream.Seek(0, SeekOrigin.Begin);

				var assembly = Assembly.Load(assemblyStream.ToArray(), pdbStream.ToArray());

				return assembly;
			}
		}

		private DependencyContextCompilationOptions GetDependencyContextCompilationOptions()
		{
			var dependencyContext = DependencyContext.Load(OperatingAssembly);

			if (dependencyContext?.CompilationOptions != null)
			{
				return dependencyContext.CompilationOptions;
			}

			return DependencyContextCompilationOptions.Default;
		}

		private CSharpCompilation CreateCompilation(string compilationContent, string assemblyName)
		{
			SourceText sourceText = SourceText.From(compilationContent, Encoding.UTF8);
			SyntaxTree syntaxTree = CreateSyntaxTree(sourceText).WithFilePath(assemblyName);

			CSharpCompilation compilation = CreateCompilation(assemblyName).AddSyntaxTrees(syntaxTree);

			compilation = ExpressionRewriter.Rewrite(compilation);
			return compilation;
		}

		public CSharpCompilation CreateCompilation(string assemblyName)
		{
			return CSharpCompilation.Create(
				assemblyName,
				options: CSharpCompilationOptions,
				references: metadataReferences);
		}

		public SyntaxTree CreateSyntaxTree(SourceText sourceText)
		{
			return CSharpSyntaxTree.ParseText(sourceText, options: ParseOptions);
		}

		private CSharpCompilationOptions GetCompilationOptions(DependencyContextCompilationOptions dependencyContextOptions)
		{
			var csharpCompilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

			// Disable 1702 until roslyn turns this off by default
			csharpCompilationOptions = csharpCompilationOptions.WithSpecificDiagnosticOptions(
				new Dictionary<string, ReportDiagnostic>
				{
					{"CS1701", ReportDiagnostic.Suppress}, // Binding redirects
                    {"CS1702", ReportDiagnostic.Suppress},
					{"CS1705", ReportDiagnostic.Suppress}
				});

			if (dependencyContextOptions.AllowUnsafe.HasValue)
			{
				csharpCompilationOptions = csharpCompilationOptions.WithAllowUnsafe(
					dependencyContextOptions.AllowUnsafe.Value);
			}

			OptimizationLevel optimizationLevel;
			if (dependencyContextOptions.Optimize.HasValue)
			{
				optimizationLevel = dependencyContextOptions.Optimize.Value ?
					OptimizationLevel.Release :
					OptimizationLevel.Debug;
			}
			else
			{
				optimizationLevel = isDevelopment ?
					OptimizationLevel.Debug :
					OptimizationLevel.Release;
			}
			csharpCompilationOptions = csharpCompilationOptions.WithOptimizationLevel(optimizationLevel);

			if (dependencyContextOptions.WarningsAsErrors.HasValue)
			{
				var reportDiagnostic = dependencyContextOptions.WarningsAsErrors.Value ?
					ReportDiagnostic.Error :
					ReportDiagnostic.Default;
				csharpCompilationOptions = csharpCompilationOptions.WithGeneralDiagnosticOption(reportDiagnostic);
			}

			return csharpCompilationOptions;
		}

		private CSharpParseOptions GetParseOptions(DependencyContextCompilationOptions dependencyContextOptions)
		{
			var configurationSymbol = isDevelopment ? "DEBUG" : "RELEASE";
			var defines = dependencyContextOptions.Defines.Concat(new[] { configurationSymbol });

			var parseOptions = new CSharpParseOptions(preprocessorSymbols: defines);

			if (!string.IsNullOrEmpty(dependencyContextOptions.LanguageVersion))
			{
				if (dependencyContextOptions.LanguageVersion.TryParse(out var languageVersion))
				{
					parseOptions = parseOptions.WithLanguageVersion(languageVersion);
				}
				else
				{
					Debug.Fail($"LanguageVersion {languageVersion} specified in the deps file could not be parsed.");
				}
			}

			return parseOptions;
		}

		private bool IsAssemblyDebugBuild(Assembly assembly)
		{
			return assembly.GetCustomAttributes(false).OfType<DebuggableAttribute>().Select(da => da.IsJITTrackingEnabled).FirstOrDefault();
		}
	}
}
