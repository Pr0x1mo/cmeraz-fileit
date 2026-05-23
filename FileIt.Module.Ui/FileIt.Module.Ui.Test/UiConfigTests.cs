namespace FileIt.Module.Ui.Test;

[TestClass]
public class UiConfigTests
{
    [TestMethod]
    public void Defaults_AreEmptyStrings()
    {
        var sut = new FileIt.Module.Ui.App.UiConfig();

        Assert.AreEqual(string.Empty, sut.QueueName);
        Assert.AreEqual(string.Empty, sut.SourceContainer);
        Assert.AreEqual(string.Empty, sut.WorkingContainer);
        Assert.AreEqual(string.Empty, sut.FinalContainer);
    }
}
