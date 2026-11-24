using nadena.dev.ndmf;
using Gokoukotori.SdfAutoGenerator.Editor;

[assembly: ExportsPlugin(typeof(SdfAutoGeneratorPlugin))]

namespace Gokoukotori.SdfAutoGenerator.Editor
{
    public sealed class SdfAutoGeneratorPlugin : Plugin<SdfAutoGeneratorPlugin>
    {
        public override string QualifiedName => "com.gokoukotori.sdf-auto-generator.ndmf";
        public override string DisplayName => "SDF Auto Generator";

        protected override void Configure()
        {
            // AOME の上書きと MA（Material Setter / Swap のカーブ化を含む）の後、MA の後段の前で動く。
            // TexTransTool の前は指定しない: TTT は Light Limit Changer の前、LLC の前半は MA の前に動くため、
            // TTT と LLC が両方あると TTT が MA より前に固定され、「MA の後かつ TTT の前」は制約ループになる
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("aoyon.material-editor")
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("nadena.dev.modular-avatar.late-transform-stages")
                .Run(SdfBuildPass.Instance)
                .PreviewingWith(new SdfPreviewFilter());
        }
    }
}
