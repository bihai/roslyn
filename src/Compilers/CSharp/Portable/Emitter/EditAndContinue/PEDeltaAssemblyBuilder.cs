﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis.CodeGen;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.Emit;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Emit
{
    internal sealed class PEDeltaAssemblyBuilder : PEAssemblyBuilderBase, IPEDeltaAssemblyBuilder
    {
        private readonly EmitBaseline previousGeneration;
        private readonly CSharpDefinitionMap previousDefinitions;
        private readonly SymbolChanges changes;

        public PEDeltaAssemblyBuilder(
            SourceAssemblySymbol sourceAssembly,
            EmitOptions emitOptions,
            OutputKind outputKind,
            ModulePropertiesForSerialization serializationProperties,
            IEnumerable<ResourceDescription> manifestResources,
            EmitBaseline previousGeneration,
            IEnumerable<SemanticEdit> edits,
            Func<ISymbol, bool> isAddedSymbol)
            : base(sourceAssembly, emitOptions, outputKind, serializationProperties, manifestResources, assemblySymbolMapper: null, additionalTypes: ImmutableArray<NamedTypeSymbol>.Empty)
        {
            var context = new EmitContext(this, null, new DiagnosticBag());
            var module = previousGeneration.OriginalMetadata;
            var compilation = sourceAssembly.DeclaringCompilation;
            var metadataAssembly = compilation.GetBoundReferenceManager().CreatePEAssemblyForAssemblyMetadata(AssemblyMetadata.Create(module), MetadataImportOptions.All);
            var metadataDecoder = new Microsoft.CodeAnalysis.CSharp.Symbols.Metadata.PE.MetadataDecoder(metadataAssembly.PrimaryModule);

            previousGeneration = EnsureInitialized(previousGeneration, metadataDecoder);

            var matchToMetadata = new CSharpSymbolMatcher(previousGeneration.AnonymousTypeMap, sourceAssembly, context, metadataAssembly);

            CSharpSymbolMatcher matchToPrevious = null;
            if (previousGeneration.Ordinal > 0)
            {
                var previousAssembly = ((CSharpCompilation)previousGeneration.Compilation).SourceAssembly;
                var previousContext = new EmitContext((PEModuleBuilder)previousGeneration.PEModuleBuilder, null, new DiagnosticBag());

                matchToPrevious = new CSharpSymbolMatcher(
                    previousGeneration.AnonymousTypeMap, 
                    sourceAssembly: sourceAssembly,
                    sourceContext: context, 
                    otherAssembly: previousAssembly, 
                    otherContext: previousContext,
                    otherSynthesizedMembersOpt: previousGeneration.SynthesizedMembers);
            }

            this.previousDefinitions = new CSharpDefinitionMap(previousGeneration.OriginalMetadata.Module, edits, metadataDecoder, matchToMetadata, matchToPrevious);
            this.previousGeneration = previousGeneration;
            this.changes = new SymbolChanges(this.previousDefinitions, edits, isAddedSymbol);
        }

        public override int CurrentGenerationOrdinal => this.previousGeneration.Ordinal + 1;

        private static IReadOnlyDictionary<AnonymousTypeKey, AnonymousTypeValue> GetAnonymousTypeMapFromMetadata(
            MetadataReader reader,
            Symbols.Metadata.PE.MetadataDecoder metadataDecoder)
        {
            var result = new Dictionary<AnonymousTypeKey, AnonymousTypeValue>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var def = reader.GetTypeDefinition(handle);
                if (!def.Namespace.IsNil)
                {
                    continue;
                }
                if (!reader.StringComparer.StartsWith(def.Name, GeneratedNames.AnonymousNamePrefix))
                {
                    continue;
                }
                var metadataName = reader.GetString(def.Name);
                short arity;
                var name = MetadataHelpers.InferTypeArityAndUnmangleMetadataName(metadataName, out arity);
                int index;
                if (GeneratedNames.TryParseAnonymousTypeTemplateName(name, out index))
                {
                    var builder = ArrayBuilder<string>.GetInstance();
                    if (TryGetAnonymousTypeKey(reader, def, builder))
                    {
                        var type = (NamedTypeSymbol)metadataDecoder.GetTypeOfToken(handle);
                        var key = new AnonymousTypeKey(builder.ToImmutable());
                        var value = new AnonymousTypeValue(name, index, type);
                        result.Add(key, value);
                    }
                    builder.Free();
                }
            }
            return result;
        }

        private static bool TryGetAnonymousTypeKey(
            MetadataReader reader,
            TypeDefinition def,
            ArrayBuilder<string> builder)
        {
            foreach (var typeParameterHandle in def.GetGenericParameters())
            {
                var typeParameter = reader.GetGenericParameter(typeParameterHandle);
                string fieldName;
                if (!GeneratedNames.TryParseAnonymousTypeParameterName(reader.GetString(typeParameter.Name), out fieldName))
                {
                    return false;
                }
                builder.Add(fieldName);
            }
            return true;
        }

        private static EmitBaseline EnsureInitialized(
            EmitBaseline previousGeneration,
            Microsoft.CodeAnalysis.CSharp.Symbols.Metadata.PE.MetadataDecoder metadataDecoder)
        {
            if (previousGeneration.AnonymousTypeMap != null)
            {
                return previousGeneration;
            }

            var anonymousTypeMap = GetAnonymousTypeMapFromMetadata(previousGeneration.MetadataReader, metadataDecoder);
            return previousGeneration.WithAnonymousTypeMap(anonymousTypeMap);
        }

        internal EmitBaseline PreviousGeneration
        {
            get { return this.previousGeneration; }
        }

        internal CSharpDefinitionMap PreviousDefinitions
        {
            get { return this.previousDefinitions; }
        }

        internal override bool SupportsPrivateImplClass
        {
            get
            {
                // Disable <PrivateImplementationDetails> in ENC since the
                // CLR does not support adding non-private members.
                return false;
            }
        }

        public IReadOnlyDictionary<AnonymousTypeKey, AnonymousTypeValue> GetAnonymousTypeMap()
        {
            var anonymousTypes = this.Compilation.AnonymousTypeManager.GetAnonymousTypeMap();
            // Should contain all entries in previous generation.
            Debug.Assert(this.previousGeneration.AnonymousTypeMap.All(p => anonymousTypes.ContainsKey(p.Key)));
            return anonymousTypes;
        }

        internal override VariableSlotAllocator TryCreateVariableSlotAllocator(MethodSymbol method)
        {
            return this.previousDefinitions.TryCreateVariableSlotAllocator(this.previousGeneration, method);
        }

        internal override ImmutableArray<AnonymousTypeKey> GetPreviousAnonymousTypes()
        {
            return ImmutableArray.CreateRange(this.previousGeneration.AnonymousTypeMap.Keys);
        }

        internal override int GetNextAnonymousTypeIndex()
        {
            return this.previousGeneration.GetNextAnonymousTypeIndex();
        }

        internal override bool TryGetAnonymousTypeName(NamedTypeSymbol template, out string name, out int index)
        {
            Debug.Assert(this.Compilation == template.DeclaringCompilation);
            return this.previousDefinitions.TryGetAnonymousTypeName(template, out name, out index);
        }

        internal SymbolChanges Changes
        {
            get { return this.changes; }
        }

        internal override IEnumerable<Cci.INamespaceTypeDefinition> GetTopLevelTypesCore(EmitContext context)
        {
            return this.changes.GetTopLevelTypes(context);
        }

        public void OnCreatedIndices(DiagnosticBag diagnostics)
        {
            var embeddedTypesManager = this.EmbeddedTypesManagerOpt;
            if (embeddedTypesManager != null)
            {
                foreach (var embeddedType in embeddedTypesManager.EmbeddedTypesMap.Keys)
                {
                    diagnostics.Add(new CSDiagnosticInfo(ErrorCode.ERR_EncNoPIAReference, embeddedType), Location.None);
                }
            }
        }

        internal override bool IsEncDelta
        {
            get { return true; }
        }
    }
}
