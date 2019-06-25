using System.Linq;
using CppSharp.AST;
using CppSharp.AST.Extensions;
using CppSharp.Generators.CSharp;
using CppSharp.Types;

namespace QtSharp
{
    [TypeMap("QFlags")]
    public class QFlags : TypeMap
    {
        public override string CSharpConstruct() => string.Empty;

        public override Type CSharpSignatureType(TypePrinterContext ctx)
        {
            var enumType = GetEnumType(ctx.Type);
            if (enumType == null)
            {
                var specializationType = ctx.Type as TemplateSpecializationType;
                if (specializationType != null)
                {
                    return new UnsupportedType($@"{specializationType.Template.Name}<{
                        string.Join(", ", specializationType.Arguments.Select(a => a.Type.Type))}>");
                }
                var template = (Class) ((TagType) ctx.Type).Declaration;
                return new UnsupportedType($@"{template.Name}<{
                    string.Join(", ", template.TemplateParameters.Select(p => p.Name))}>");
            }
            return GetEnumType(ctx.Type);
        }

        public override void CSharpMarshalToNative(CSharpMarshalContext ctx)
        {
            if (ctx.Parameter.Type.Desugar().IsAddress())
                ctx.Return.Write("new global::System.IntPtr(&{0})", ctx.Parameter.Name);
            else
                ctx.Return.Write(ctx.Parameter.Name);
        }

        public override void CSharpMarshalToManaged(CSharpMarshalContext ctx)
        {
            if (ctx.ReturnType.Type.Desugar().IsAddress())
            {
                var finalType = ctx.ReturnType.Type.GetFinalPointee() ?? ctx.ReturnType.Type;
                var enumType = GetEnumType(finalType);
                ctx.Return.Write("*({0}*) {1}", enumType, ctx.ReturnVarName);
            }
            else
            {
                ctx.Return.Write(ctx.ReturnVarName);
            }
        }

        public override bool IsIgnored => Type.IsDependent;

        private static Type GetEnumType(Type mappedType)
        {
            var type = mappedType.Desugar();
            ClassTemplateSpecialization classTemplateSpecialization;
            var templateSpecializationType = type as TemplateSpecializationType;
            if (templateSpecializationType != null)
                classTemplateSpecialization = templateSpecializationType.GetClassTemplateSpecialization();
            else
                classTemplateSpecialization = ((TagType) type).Declaration as ClassTemplateSpecialization;
            if (classTemplateSpecialization == null)
            {
                return null;
            }
            return classTemplateSpecialization.Arguments[0].Type.Type;
        }
    }
}
