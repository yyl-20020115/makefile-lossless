using MakefileLossless;
using Xunit;

namespace MakefileLossless.Tests;

public sealed class VariantParsingTests
{
    [Theory]
    [InlineData("all:\r\n\techo hi\r\n", MakefileVariant.GnuMake)]
    [InlineData(".if 1\nall:\n\techo hi\n.endif\n", MakefileVariant.BsdMake)]
    [InlineData("!IF 1\nall:\n\techo hi\n!ENDIF\n", MakefileVariant.NMake)]
    [InlineData("all:\n\techo hi\n", MakefileVariant.PosixMake)]
    public void ReconstructsExactInputForEachVariant(string input, MakefileVariant variant)
    {
        var parsed = Makefile.ParseWithVariant(input, variant);
        Assert.Equal(input, parsed.ToString());
        Assert.Equal(variant, parsed.Variant);
    }

    [Fact]
    public void AutoDetectsNMakeDirectives()
    {
        var parsed = Makefile.Parse("!IFDEF DEBUG\nCFLAGS = /Zi\n!ENDIF\n");
        Assert.Equal(MakefileVariant.NMake, parsed.Variant);
        Assert.True(parsed.VariantDetected);
        Assert.DoesNotContain(parsed.Errors, error => error.Message.Contains("nmake directive", StringComparison.Ordinal));
    }

    [Fact]
    public void NMakeCaretContinuesOnlyMacroDefinitions()
    {
        var macro = Makefile.ParseWithVariant("FLAGS = /DDEBUG^\n /O2\n", MakefileVariant.NMake);
        Assert.Single(macro.Items);
        Assert.Equal("FLAGS = /DDEBUG^\n /O2\n", macro.Items[0].Text);

        var rule = Makefile.ParseWithVariant("all: dep^\n\techo done\n", MakefileVariant.NMake);
        Assert.Equal(2, rule.Items.Count);
        Assert.Equal(MakefileItemKind.Rule, rule.Items[0].Kind);
        Assert.Equal(MakefileItemKind.Recipe, rule.Items[1].Kind);
    }

    [Fact]
    public void SpaceIndentedNMakeCommandsAreRecipesButNotGnuRecipes()
    {
        var nmake = Makefile.ParseWithVariant("all:\n  echo hi\n", MakefileVariant.NMake);
        Assert.Equal(MakefileItemKind.Recipe, nmake.Items[1].Kind);

        var gnu = Makefile.ParseWithVariant("all:\n  echo hi\n", MakefileVariant.GnuMake);
        Assert.NotEqual(MakefileItemKind.Recipe, gnu.Items[1].Kind);
        Assert.Contains(gnu.Errors, error => error.Line == 1);
    }

    [Fact]
    public void RejectsForeignConditionalSyntaxAndUnclosedBlocks()
    {
        var gnu = Makefile.ParseWithVariant(".if 1\n.endif\n", MakefileVariant.GnuMake);
        Assert.Contains(gnu.Errors, error => error.Message.Contains("BSD conditional", StringComparison.Ordinal));

        var posix = Makefile.ParseWithVariant("ifdef X\nendif\n", MakefileVariant.PosixMake);
        Assert.Contains(posix.Errors, error => error.Message.Contains("POSIX", StringComparison.Ordinal));

        var bsd = Makefile.ParseWithVariant(".if 1\nall:\n", MakefileVariant.BsdMake);
        Assert.Contains(bsd.Errors, error => error.Message.Contains("not closed", StringComparison.Ordinal));
    }

    [Fact]
    public void RecognizesVariantSpecificIncludeAndDirectiveForms()
    {
        Assert.Equal(MakefileItemKind.Include,
            Makefile.ParseWithVariant(".include \"config.mk\"\n", MakefileVariant.BsdMake).Items[0].Kind);
        Assert.Equal(MakefileItemKind.Include,
            Makefile.ParseWithVariant("!INCLUDE <win32.mak>\n", MakefileVariant.NMake).Items[0].Kind);
        Assert.Equal(MakefileItemKind.Include,
            Makefile.ParseWithVariant("-include optional.mk\n", MakefileVariant.GnuMake).Items[0].Kind);
        Assert.Equal(MakefileItemKind.Directive,
            Makefile.ParseWithVariant(".undef FOO\n", MakefileVariant.BsdMake).Items[0].Kind);
    }

    [Fact]
    public void RecipePrefixAssignmentIsGnuSpecific()
    {
        var gnu = Makefile.ParseWithVariant(".RECIPEPREFIX = >\nall:\n>echo hi\n", MakefileVariant.GnuMake);
        Assert.Equal(MakefileItemKind.Recipe, gnu.Items[2].Kind);

        var bsd = Makefile.ParseWithVariant(".RECIPEPREFIX = >\nall:\n>echo hi\n", MakefileVariant.BsdMake);
        Assert.NotEqual(MakefileItemKind.Recipe, bsd.Items[2].Kind);
    }
}
