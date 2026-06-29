using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

public class ActivityTemplate_PlayModeSmokeTest
{
    [UnityTest]
    public IEnumerator PlayModeSmokeTest_ShouldPass()
    {
        yield return null;

        Assert.Pass("Play Mode test setup is working.");
    }
}