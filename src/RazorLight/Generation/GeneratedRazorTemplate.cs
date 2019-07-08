using Microsoft.AspNetCore.Razor.Language;
using RazorLight.Razor;
using System;

namespace RazorLight.Generation
{
    public class GeneratedRazorTemplate : IGeneratedRazorTemplate
    {
        public GeneratedRazorTemplate(RazorLightProjectItem projectItem, RazorCSharpDocument cSharpDocument)
        {
            ProjectItem = projectItem ?? throw new ArgumentNullException(nameof(projectItem));
            CSharpDocument = cSharpDocument ?? throw new ArgumentNullException(nameof(cSharpDocument));
        }

        public RazorLightProjectItem ProjectItem { get; set; }

        public string TemplateKey => ProjectItem.Key;

        public RazorCSharpDocument CSharpDocument { get; set; }

        public string GeneratedCode => CSharpDocument.GeneratedCode;
    }
}
