using System.Text.Json;

namespace PlayMap.Engine.Tests.Show;

/// <summary>The node's own assistant asks the node's Show Mode before it runs or queues a line (through the assistant service's
/// endpoint, as the running node wires it): armed, a line the table refuses an assistant is neither run nor queued; a draft the
/// assistant opens takes its edits off the air, whatever its settings say about review lines.</summary>
public sealed class AssistantShowTests
{
    [Fact]
    public async Task ArmedARefusedLineIsNeitherRunNorQueued()
    {
        var model = new ScriptedModel()
            .Then(ScriptedModel.Propose("c1", "scan the room", "BLEND SOLVE ALL"))
            .Then(ScriptedModel.Say("Refused."));
        var rig = new OpsRig(model: model);
        try
        {
            await rig.Ok("CREDENTIAL SET assistant sk-ant-test-key-0123456789");
            await rig.Ok("SHOW ARM");
            var reply = JsonDocument.Parse(await rig.Ok("ASK solve the blend")).RootElement;
            var line = reply.GetProperty("lines")[0];
            Assert.Equal("refused", line.GetProperty("status").GetString());
            Assert.StartsWith("Not while armed: BLEND SOLVE", line.GetProperty("reply").GetString(), StringComparison.Ordinal);
            Assert.Empty(rig.Assistant.Queue.All);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task TheAssistantsOwnDraftTakesItsEditsOffTheAir()
    {
        var model = new ScriptedModel()
            .Then(ScriptedModel.Propose("c1", "stage a rename", "BLIND ON", "SET op_1 name \"Stage left\""))
            .Then(ScriptedModel.Say("Staged."));
        var rig = new OpsRig(model: model);
        try
        {
            await rig.Ok("CREDENTIAL SET assistant sk-ant-test-key-0123456789");
            var reply = JsonDocument.Parse(await rig.Ok("ASK stage a rename")).RootElement;
            Assert.Equal(["ran", "ran"], reply.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("status").GetString()));
            Assert.Empty(rig.Assistant.Queue.All);
            Assert.Equal("Output 1", rig.Engine.Store.Project.FindOutput("op_1")!.Name);
            Assert.Equal("Stage left", rig.Engine.Snapshot.Draft!.Project.FindOutput("op_1")!.Name);
            Assert.StartsWith("Assistant for", rig.Engine.BlindOwnerName, StringComparison.Ordinal);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
