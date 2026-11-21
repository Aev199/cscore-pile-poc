using CScore.Sp63Shear;
using Xunit;

public class InclinedMomentNativeTests
{
    // Direct native control: h0=0.55 m, Ns=600 kN, qsw=100 kN/m, k=1.
    private static ShearInclinedInput Input() => new(
        .4,.55,15300,1035,100,.2,600,ElementKind.Other,1,
        10,.0055,0,[],true,null);

    private static InclinedSectionGeometry Side(bool positive) => new(
        .4,.55,600,.0015,15300,1035,.24,.003,32500000,.002,.0001,
        ShearPlane.Vy,positive,[]);

    private static InclinedSectionGeometryPair Geometry() => new(Side(true),Side(false));

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void ConstantExternalMomentMatchesHandControl(int sign)
    {
        var result=ShearInclinedChecker.Check(Input(),
            new ConstantProfile(sign*100,sign*150,-1000,3),Geometry(),1);
        var check=result.Details.Single(d=>d.Formula=="8.63");
        // Critical C=h0: Ms=297 kNm, Msw=15.125 kNm, total=312.125 kNm.
        Assert.Equal(.55,check.Variables["C"],10);
        Assert.Equal(297,check.Variables["Ms"],8);
        Assert.Equal(15.125,check.Variables["Msw"],8);
        Assert.Equal(150,check.Applied,8);
        Assert.Equal(312.125,check.Allowable,8);
        Assert.Equal(150.0/312.125,check.Ratio,10);
    }

    [Fact]
    public void SampledExternalMomentIsEvaluatedAtOppositeCrackEnd()
    {
        // M(s)=150+100s follows equilibrium with Q=100. Check station s=0 only.
        var profile=new SampledProfile([
            new ForceSample(0,100,150,-1000),new ForceSample(3,100,450,-1000)],0,3);
        Assert.Empty(profile.Warnings);
        var result=ShearInclinedChecker.Check(Input(),profile,Geometry(),1);
        var check=result.Details.Single(d=>d.Formula=="8.63");
        // Critical C=2h0=1.1 m: M(Point0)=260, Ms+Msw=297+60.5=357.5 kNm.
        Assert.Equal(0,check.Variables["s"],10);
        Assert.Equal(1.1,check.Variables["C"],10);
        Assert.Equal(260,check.Applied,8);
        Assert.Equal(357.5,check.Allowable,8);
        Assert.Equal(260.0/357.5,check.Ratio,10);
    }
}
