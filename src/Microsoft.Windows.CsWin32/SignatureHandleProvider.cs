// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Windows.CsWin32
{
    using System;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Metadata;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    internal class SignatureHandleProvider : ISignatureTypeProvider<TypeHandleInfo, SignatureHandleProvider.IGenericContext?>
    {
        internal static readonly SignatureHandleProvider Instance = new SignatureHandleProvider();

        private SignatureHandleProvider()
        {
        }

        internal interface IGenericContext
        {
        }

        public TypeHandleInfo GetArrayType(TypeHandleInfo elementType, ArrayShape shape) => new ArrayTypeHandleInfo(elementType, shape);

        public TypeHandleInfo GetPointerType(TypeHandleInfo elementType) => new PointerTypeHandleInfo(elementType);

        public TypeHandleInfo GetPrimitiveType(PrimitiveTypeCode typeCode) => new PrimitiveTypeHandleInfo(typeCode);

        public TypeHandleInfo GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => new HandleTypeHandleInfo(handle);

        public TypeHandleInfo GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => new HandleTypeHandleInfo(handle);

        /// <inheritdoc/>
        public TypeHandleInfo GetSZArrayType(TypeHandleInfo elementType) => throw new NotImplementedException();

        /// <inheritdoc/>
        public TypeHandleInfo GetTypeFromSpecification(MetadataReader reader, IGenericContext? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => throw new NotImplementedException();

        /// <inheritdoc/>
        public TypeHandleInfo GetByReferenceType(TypeHandleInfo elementType) => throw new NotImplementedException();

        /// <inheritdoc/>
        public TypeHandleInfo GetFunctionPointerType(MethodSignature<TypeHandleInfo> signature) => throw new NotImplementedException();

        /// <inheritdoc/>
        public TypeHandleInfo GetGenericInstantiation(TypeHandleInfo genericType, ImmutableArray<TypeHandleInfo> typeArguments) => throw new NotImplementedException();

        /// <inheritdoc/>
        public TypeHandleInfo GetGenericMethodParameter(IGenericContext? genericContext, int index) => throw new NotImplementedException();

        /// <inheritdoc/>
        public TypeHandleInfo GetGenericTypeParameter(IGenericContext? genericContext, int index) => throw new NotImplementedException();

        /// <inheritdoc/>
        public TypeHandleInfo GetModifiedType(TypeHandleInfo modifier, TypeHandleInfo unmodifiedType, bool isRequired) => throw new NotImplementedException();

        /// <inheritdoc/>
        public TypeHandleInfo GetPinnedType(TypeHandleInfo elementType) => throw new NotImplementedException();

        internal static TypeSyntax ToTypeSyntax(PrimitiveTypeCode typeCode, bool preferNativeInt)
        {
            return typeCode switch
            {
                PrimitiveTypeCode.Char => PredefinedType(Token(SyntaxKind.CharKeyword)),
                PrimitiveTypeCode.Boolean => PredefinedType(Token(SyntaxKind.BoolKeyword)),
                PrimitiveTypeCode.SByte => PredefinedType(Token(SyntaxKind.SByteKeyword)),
                PrimitiveTypeCode.Byte => PredefinedType(Token(SyntaxKind.ByteKeyword)),
                PrimitiveTypeCode.Int16 => PredefinedType(Token(SyntaxKind.ShortKeyword)),
                PrimitiveTypeCode.UInt16 => PredefinedType(Token(SyntaxKind.UShortKeyword)),
                PrimitiveTypeCode.Int32 => PredefinedType(Token(SyntaxKind.IntKeyword)),
                PrimitiveTypeCode.UInt32 => PredefinedType(Token(SyntaxKind.UIntKeyword)),
                PrimitiveTypeCode.Int64 => PredefinedType(Token(SyntaxKind.LongKeyword)),
                PrimitiveTypeCode.UInt64 => PredefinedType(Token(SyntaxKind.ULongKeyword)),
                PrimitiveTypeCode.Single => PredefinedType(Token(SyntaxKind.FloatKeyword)),
                PrimitiveTypeCode.Double => PredefinedType(Token(SyntaxKind.DoubleKeyword)),
                PrimitiveTypeCode.Object => PredefinedType(Token(SyntaxKind.ObjectKeyword)),
                PrimitiveTypeCode.String => PredefinedType(Token(SyntaxKind.StringKeyword)),
                PrimitiveTypeCode.IntPtr => preferNativeInt ? IdentifierName("nint") : IdentifierName(nameof(IntPtr)),
                PrimitiveTypeCode.UIntPtr => preferNativeInt ? IdentifierName("nuint") : IdentifierName(nameof(UIntPtr)),
                PrimitiveTypeCode.Void => PredefinedType(Token(SyntaxKind.VoidKeyword)),
                _ => throw new NotSupportedException("Unsupported type code: " + typeCode),
            };
        }
    }

    internal record TypeSyntaxParameters(Generator Generator, bool PreferNativeInt, bool PreferMarshaledTypes, bool UseComInterfaces, bool QualifyNames)
    {
        internal MetadataReader Reader => this.Generator.Reader;
    }

    internal abstract record TypeHandleInfo
    {
        internal abstract TypeSyntax ToTypeSyntax(TypeSyntaxParameters inputs);
    }

    internal record PrimitiveTypeHandleInfo(PrimitiveTypeCode PrimitiveTypeCode) : TypeHandleInfo
    {
        internal override TypeSyntax ToTypeSyntax(TypeSyntaxParameters inputs) => SignatureHandleProvider.ToTypeSyntax(this.PrimitiveTypeCode, inputs.PreferNativeInt);
    }

    internal record HandleTypeHandleInfo(EntityHandle Handle) : TypeHandleInfo
    {
        internal override TypeSyntax ToTypeSyntax(TypeSyntaxParameters inputs)
        {
            NameSyntax? nameSyntax;
            bool? isInterface = null;
            switch (this.Handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    TypeDefinition td = inputs.Reader.GetTypeDefinition((TypeDefinitionHandle)this.Handle);
                    nameSyntax = inputs.QualifyNames ? GetNestingQualifiedName(inputs.Reader, td) : IdentifierName(inputs.Reader.GetString(td.Name));
                    isInterface = (td.Attributes & TypeAttributes.Interface) == TypeAttributes.Interface;
                    break;
                case HandleKind.TypeReference:
                    TypeReference tr = inputs.Reader.GetTypeReference((TypeReferenceHandle)this.Handle);
                    nameSyntax = inputs.QualifyNames ? GetNestingQualifiedName(inputs.Reader, tr) : IdentifierName(inputs.Reader.GetString(tr.Name));
                    break;
                default:
                    throw new NotSupportedException("Unrecognized handle type.");
            }

            string simpleName = (nameSyntax is QualifiedNameSyntax qname ? qname.Right : (SimpleNameSyntax)nameSyntax).Identifier.ValueText;
            if (IsMarshaledAsObject(inputs, simpleName))
            {
                return PredefinedType(Token(SyntaxKind.ObjectKeyword)).WithAdditionalAnnotations(Generator.IsManagedTypeAnnotation);
            }

            // Take this opportunity to ensure the type exists too.
            if (Generator.BclInteropStructs.TryGetValue(simpleName, out TypeSyntax? bclType))
            {
                return bclType;
            }

            if (inputs.PreferMarshaledTypes && Generator.AdditionalBclInteropStructsMarshaled.TryGetValue(simpleName, out bclType))
            {
                return bclType;
            }

            this.RequestTypeGeneration(inputs.Generator);

            TypeSyntax syntax = nameSyntax;

            if (isInterface is true)
            {
                syntax = inputs.UseComInterfaces ? syntax.WithAdditionalAnnotations(Generator.IsManagedTypeAnnotation) : PointerType(syntax);
            }

            return syntax;
        }

        private static NameSyntax GetNestingQualifiedName(MetadataReader reader, TypeDefinitionHandle handle) => GetNestingQualifiedName(reader, reader.GetTypeDefinition(handle));

        private static NameSyntax GetNestingQualifiedName(MetadataReader reader, TypeDefinition td)
        {
            IdentifierNameSyntax name = IdentifierName(reader.GetString(td.Name));
            return td.GetDeclaringType() is { IsNil: false } nestingType ? QualifiedName(GetNestingQualifiedName(reader, nestingType), name) : name;
        }

        private static NameSyntax GetNestingQualifiedName(MetadataReader reader, TypeReferenceHandle handle) => GetNestingQualifiedName(reader, reader.GetTypeReference(handle));

        private static NameSyntax GetNestingQualifiedName(MetadataReader reader, TypeReference tr)
        {
            SimpleNameSyntax typeName = IdentifierName(reader.GetString(tr.Name));
            return tr.ResolutionScope.Kind == HandleKind.TypeReference
                ? QualifiedName(GetNestingQualifiedName(reader, (TypeReferenceHandle)tr.ResolutionScope), typeName)
                : typeName;
        }

        private static bool IsMarshaledAsObject(TypeSyntaxParameters inputs, string name)
        {
            return inputs.UseComInterfaces && name is "IUnknown" or "IDispatch" or "VARIANT";
        }

        private void RequestTypeGeneration(Generator generator)
        {
            if (this.Handle.Kind == HandleKind.TypeDefinition)
            {
                generator.RequestInteropType((TypeDefinitionHandle)this.Handle);
            }
            else if (this.Handle.Kind == HandleKind.TypeReference)
            {
                generator.RequestInteropType((TypeReferenceHandle)this.Handle);
            }
        }
    }

    internal record ArrayTypeHandleInfo(TypeHandleInfo ElementType, ArrayShape Shape) : TypeHandleInfo, ITypeHandleContainer
    {
        internal override TypeSyntax ToTypeSyntax(TypeSyntaxParameters inputs) => ArrayType(this.ElementType.ToTypeSyntax(inputs), SingletonList(ArrayRankSpecifier().AddSizes(this.Shape.Sizes.Select(size => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(size))).ToArray<ExpressionSyntax>())));
    }

    internal record PointerTypeHandleInfo(TypeHandleInfo ElementType) : TypeHandleInfo, ITypeHandleContainer
    {
        internal override TypeSyntax ToTypeSyntax(TypeSyntaxParameters inputs) => PointerType(this.ElementType.ToTypeSyntax(inputs));
    }

#pragma warning disable SA1201 // Elements should appear in the correct order
    internal interface ITypeHandleContainer
#pragma warning restore SA1201 // Elements should appear in the correct order
    {
        TypeHandleInfo ElementType { get; }
    }
}
