using DotnetCqrs.Host;

namespace DotnetCqrs.Tests;

/// <summary>
/// The node-identity contract (<c>lab/cqrs-system-contracts/contracts/node-identity.md</c>,
/// 1.0). <see cref="Every_combination_of_inputs_resolves_as_the_contract_table_says"/> runs all
/// 24 input combinations against the contract's seven-row resolution table, copied below; the
/// rest check the same behaviour against a real directory, including what the file looks like
/// afterwards.
/// </summary>
public class NodeIdentityTests : IDisposable
{
    private readonly string _root;

    public NodeIdentityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-node-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private const string Assigned = "orchestrator-assigned_7";
    private const string Stored = "0192b5c4-7e1a-7c3e-9f00-5b2d8a1c4e77";

    // ---------------------------------------------------------------- the resolution table

    /// <summary>The contract's resolution rows, verbatim: env, file, dir, then the outcome.
    /// "any" matches every value; a null identity fails the boot.</summary>
    private static readonly (string Env, string File, string Dir, string? Identity, string NodeId, bool Writes)[] ContractRows =
    [
        ("valid", "any", "any", "assigned", "env", false),
        ("invalid", "any", "any", null, "", false),
        ("unset", "valid", "any", "persistent", "file", false),
        ("unset", "absent", "writable", "persistent", "new", true),
        ("unset", "absent", "unwritable", "ephemeral", "new", false),
        ("unset", "invalid", "any", "ephemeral", "new", false),
        ("unset", "unreadable", "any", "ephemeral", "new", false),
    ];

    public static TheoryData<string, string, string> AllCombinations()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var env in new[] { "unset", "valid", "invalid" })
            foreach (var file in new[] { "absent", "valid", "invalid", "unreadable" })
                foreach (var dir in new[] { "writable", "unwritable" })
                    data.Add(env, file, dir);
        return data;
    }

    [Fact]
    public void The_combinations_are_the_contracts_24_and_each_matches_exactly_one_row()
    {
        Assert.Equal(24, AllCombinations().Count());
        foreach (var combo in AllCombinations())
            Assert.Single(ContractRows, r => Matches(r, (string)combo[0], (string)combo[1], (string)combo[2]));
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Every_combination_of_inputs_resolves_as_the_contract_table_says(string env, string file, string dir)
    {
        var row = ContractRows.Single(r => Matches(r, env, file, dir));
        var stateDir = new FakeStateDirectory(file, writable: dir == "writable");
        var assigned = env switch { "valid" => Assigned, "invalid" => "not.valid", _ => null };

        if (row.Identity is null)
        {
            Assert.Throws<InvalidNodeIdException>(() => NodeIdentity.Resolve(assigned, stateDir));
            Assert.Equal(0, stateDir.Reads + stateDir.Writes);
            return;
        }

        var resolution = NodeIdentity.Resolve(assigned, stateDir);

        Assert.Equal(row.Identity, new NodeIdentity(resolution.NodeId, resolution.Identity, "i", "h", "dotnetcqrs", "writer", default).IdentityValue);
        Assert.True(NodeIdentity.IsValidNodeId(resolution.NodeId));
        switch (row.NodeId)
        {
            case "env": Assert.Equal(Assigned, resolution.NodeId); break;
            case "file": Assert.Equal(Stored, resolution.NodeId); break;
            default: Assert.NotEqual(Stored, resolution.NodeId); Assert.NotEqual(Assigned, resolution.NodeId); break;
        }
        // An assignment neither reads nor writes the file; nothing but the one generating row writes.
        if (env == "valid") Assert.Equal(0, stateDir.Reads);
        Assert.Equal(row.Writes ? 1 : 0, stateDir.Writes);
        if (row.Writes) Assert.Equal(resolution.NodeId, stateDir.Written);
        // Ephemeral is never silent; an unusable file is an error, not a warning.
        Assert.Equal(row.Identity == "ephemeral", resolution.Notice is not null);
        Assert.Equal(file is "invalid" or "unreadable" && env == "unset", resolution.NoticeIsError);
    }

    private static bool Matches((string Env, string File, string Dir, string? Identity, string NodeId, bool Writes) row, string env, string file, string dir) =>
        row.Env == env && (row.File == "any" || row.File == file) && (row.Dir == "any" || row.Dir == dir);

    private sealed class FakeStateDirectory(string file, bool writable) : INodeStateDirectory
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public string? Written { get; private set; }
        public string NodeIdPath => "/state/node-id";

        public StoredNodeId ReadNodeId()
        {
            Reads++;
            return file switch
            {
                "valid" => new(StoredNodeIdState.Valid, Stored),
                "invalid" => new(StoredNodeIdState.Invalid),
                "unreadable" => new(StoredNodeIdState.Unreadable, Detail: "access denied"),
                _ => new(StoredNodeIdState.Absent),
            };
        }

        public bool TryCreateNodeId(string nodeId, out string? reason)
        {
            if (!writable)
            {
                reason = "read-only";
                return false;
            }
            Writes++;
            Written = nodeId;
            reason = null;
            return true;
        }
    }

    // ---------------------------------------------------------------- on disk

    private DiskNodeStateDirectory StateDir(string name = "state") => new(Path.Combine(_root, name));

    [Fact]
    public void A_first_boot_writes_a_uuidv7_atomically_and_a_restart_reads_it_back()
    {
        var dir = StateDir();

        var first = NodeIdentity.Resolve(null, dir);
        var second = NodeIdentity.Resolve(null, dir);

        Assert.Equal(NodeIdentitySource.Persistent, first.Identity);
        Assert.Null(first.Notice);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", first.NodeId);
        Assert.Equal(first.NodeId + "\n", File.ReadAllText(dir.NodeIdPath));
        // Only node-id itself: no temporary file left behind by the atomic write.
        Assert.Equal([dir.NodeIdPath], Directory.GetFiles(dir.Path));

        Assert.Equal(first.NodeId, second.NodeId);
        Assert.Equal(NodeIdentitySource.Persistent, second.Identity);
    }

    [Fact]
    public void Same_node_restarted_keeps_its_node_id_and_reports_a_new_started_at()
    {
        var dir = StateDir();
        var before = DateTimeOffset.Parse("2026-09-27T01:40:12.345Z");
        var after = before.AddMinutes(3);

        var run1 = NodeIdentity.Resolve(null, dir);
        var run2 = NodeIdentity.Resolve(null, dir);
        var id1 = new NodeIdentity(run1.NodeId, run1.Identity, "timesheets", "node-3", NodeIdentity.StackName, "writer", before);
        var id2 = new NodeIdentity(run2.NodeId, run2.Identity, "timesheets", "node-3", NodeIdentity.StackName, "writer", after);

        Assert.Equal(id1.NodeId, id2.NodeId);
        Assert.NotEqual(id1.StartedAtValue, id2.StartedAtValue);
        Assert.Equal("2026-09-27T01:40:12.345Z", id1.StartedAtValue);
    }

    [Fact]
    public void A_stored_id_is_read_with_surrounding_whitespace_ignored()
    {
        var dir = StateDir();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(dir.NodeIdPath, $"  {Stored}\r\n");

        var resolution = NodeIdentity.Resolve(null, dir);

        Assert.Equal(Stored, resolution.NodeId);
        Assert.Equal(NodeIdentitySource.Persistent, resolution.Identity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("has.a.dot")]
    [InlineData("0192b5c4 7e1a")]
    public void An_invalid_file_gives_an_ephemeral_id_and_is_left_byte_identical(string content)
    {
        var dir = StateDir();
        Directory.CreateDirectory(dir.Path);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(dir.NodeIdPath, bytes);

        var first = NodeIdentity.Resolve(null, dir);
        var second = NodeIdentity.Resolve(null, dir);

        Assert.Equal(NodeIdentitySource.Ephemeral, first.Identity);
        Assert.True(first.NoticeIsError);
        Assert.Contains(dir.NodeIdPath, first.Notice);
        Assert.NotEqual(first.NodeId, second.NodeId); // ephemeral: new every boot until fixed
        Assert.Equal(bytes, File.ReadAllBytes(dir.NodeIdPath));
        Assert.Equal([dir.NodeIdPath], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void An_unreadable_node_id_is_ephemeral_and_left_alone()
    {
        // A directory where the file should be: present, but reading it as the file fails --
        // the unreadable case, on any OS and without permissions games.
        var dir = StateDir();
        Directory.CreateDirectory(dir.NodeIdPath);

        var resolution = NodeIdentity.Resolve(null, dir);

        Assert.Equal(NodeIdentitySource.Ephemeral, resolution.Identity);
        Assert.True(resolution.NoticeIsError);
        Assert.Contains("unreadable", resolution.Notice);
        Assert.True(Directory.Exists(dir.NodeIdPath));
    }

    [Fact]
    public void An_unwritable_state_directory_gives_an_ephemeral_id_with_a_warning()
    {
        // The state directory path is an existing file, so nothing can be created under it.
        var blocker = Path.Combine(_root, "state");
        File.WriteAllText(blocker, "not a directory");

        var resolution = NodeIdentity.Resolve(null, new DiskNodeStateDirectory(blocker));

        Assert.Equal(NodeIdentitySource.Ephemeral, resolution.Identity);
        Assert.False(resolution.NoticeIsError);
        Assert.NotNull(resolution.Notice);
        Assert.Equal("not a directory", File.ReadAllText(blocker));
        Assert.Equal([blocker], Directory.GetFiles(_root));
    }

    [Fact]
    public void No_state_directory_configured_is_ephemeral_and_names_the_setting()
    {
        var resolution = NodeIdentity.Resolve(null, null);

        Assert.Equal(NodeIdentitySource.Ephemeral, resolution.Identity);
        Assert.Contains(NodeIdentity.StateDirVariable, resolution.Notice);
    }

    [Fact]
    public void An_assigned_id_wins_over_a_different_stored_one_and_never_touches_the_file()
    {
        var dir = StateDir();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(dir.NodeIdPath, Stored + "\n");

        var assigned = NodeIdentity.Resolve(Assigned, dir);
        var unassignedAgain = NodeIdentity.Resolve(null, dir);

        Assert.Equal((Assigned, NodeIdentitySource.Assigned), (assigned.NodeId, assigned.Identity));
        Assert.Equal(Stored + "\n", File.ReadAllText(dir.NodeIdPath));
        // Removing the assignment returns the node to its stored id.
        Assert.Equal((Stored, NodeIdentitySource.Persistent), (unassignedAgain.NodeId, unassignedAgain.Identity));
    }

    [Fact]
    public void An_assigned_id_writes_nothing_even_when_no_file_exists()
    {
        var dir = StateDir();

        NodeIdentity.Resolve(Assigned, dir);

        Assert.False(Directory.Exists(dir.Path));
    }

    [Theory]
    [InlineData("has.a.dot")]
    [InlineData("has space")]
    [InlineData("wild*card")]
    [InlineData("wild>card")]
    [InlineData("slash/ed")]
    [InlineData("café")]
    [InlineData(" ")]
    public void An_invalid_assigned_id_fails_the_boot_and_says_why(string value)
    {
        var dir = StateDir();

        var ex = Assert.Throws<InvalidNodeIdException>(() => NodeIdentity.Resolve(value, dir));

        Assert.Contains(NodeIdentity.NodeIdVariable, ex.Message);
        Assert.False(Directory.Exists(dir.Path));
    }

    [Fact]
    public void The_format_accepts_64_characters_and_rejects_65()
    {
        Assert.True(NodeIdentity.IsValidNodeId(new string('a', 64)));
        Assert.False(NodeIdentity.IsValidNodeId(new string('a', 65)));
        Assert.True(NodeIdentity.IsValidNodeId("A-z_0-9"));
    }

    [Fact]
    public void An_empty_assignment_counts_as_unset()
    {
        var resolution = NodeIdentity.Resolve("", StateDir());

        Assert.Equal(NodeIdentitySource.Persistent, resolution.Identity);
    }

    [Fact]
    public void Descriptive_attributes_sit_beside_the_id_and_report_in_the_contracts_forms()
    {
        var identity = new NodeIdentity(Stored, NodeIdentitySource.Persistent, "timesheets", "node-3",
            NodeIdentity.StackName, "writer", new DateTimeOffset(2026, 9, 27, 1, 40, 12, 345, TimeSpan.FromHours(1)));

        Assert.Equal("persistent", identity.IdentityValue);
        Assert.Equal("dotnetcqrs", identity.Stack);
        Assert.Equal("2026-09-27T00:40:12.345Z", identity.StartedAtValue); // converted to UTC
        Assert.Equal(
            $"node identity: node_id={Stored} identity=persistent instance=timesheets host=node-3 stack=dotnetcqrs role=writer started_at=2026-09-27T00:40:12.345Z",
            identity.ToString());
    }

    // Contract I6: CQRS_INSTANCE if set, else the workload's own name; same format as a node id.
    [Theory]
    [InlineData(null, "OrderFulfillment")]
    [InlineData("", "OrderFulfillment")]
    [InlineData("timesheets", "timesheets")]
    public void Instance_is_the_configured_name_else_the_workloads_own(string? configured, string expected) =>
        Assert.Equal(expected, NodeIdentity.ResolveInstance(configured, "OrderFulfillment"));

    [Theory]
    [InlineData("has space")]
    [InlineData("a.b")]
    public void An_invalid_instance_fails_the_boot(string configured)
    {
        var ex = Assert.Throws<InvalidInstanceException>(() => NodeIdentity.ResolveInstance(configured, "OrderFulfillment"));
        Assert.IsAssignableFrom<InvalidIdentitySettingException>(ex);
        Assert.Contains(NodeIdentity.InstanceVariable, ex.Message);
    }

    [Fact]
    public void An_invalid_node_id_is_an_identity_setting_error_too() =>
        Assert.IsAssignableFrom<InvalidIdentitySettingException>(
            Assert.Throws<InvalidNodeIdException>(() => NodeIdentity.Resolve("not valid", null)));

    // Contract I7: an unreadable or empty hostname is "unknown", with a warning; never a boot failure.
    [Fact]
    public void An_unreadable_hostname_is_reported_as_unknown_with_a_warning()
    {
        var logged = new List<string>();

        var host = NodeIdentity.HostName(() => throw new System.Net.Sockets.SocketException(), logged.Add);

        Assert.Equal(NodeIdentity.UnknownHost, host);
        Assert.Single(logged);
    }

    [Fact]
    public void An_empty_hostname_is_reported_as_unknown_with_a_warning()
    {
        var logged = new List<string>();

        Assert.Equal(NodeIdentity.UnknownHost, NodeIdentity.HostName(() => "", logged.Add));
        Assert.Single(logged);
    }

    [Fact]
    public void A_readable_hostname_is_reported_as_is() =>
        Assert.Equal("node-3", NodeIdentity.HostName(() => "node-3"));
}
