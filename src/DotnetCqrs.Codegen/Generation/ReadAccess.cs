namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Who may read which rows of a read model (schema 3.2.0 <c>selfAccess</c>, 3.8.0
/// <c>grantsAccess</c> and <c>selfAccess.param</c>). Shared, like <see cref="DateRangeResolver"/>,
/// by the generated query route (<see cref="ReadModelQueryGenerator"/>) and the scenario-verify
/// harness, so a stateView scenario checks the rule the route actually applies.
///
/// <para><b>The rule.</b>
/// <list type="bullet">
/// <item>A caller holding <c>requiredRole</c> is <see cref="Standing.Unrestricted"/>: every row,
/// narrowed only by the params they send.</item>
/// <item>Any other caller, on a read model with access rules, is <see cref="Standing.Restricted"/>
/// to the rows whose subject column is their subject id, together with the rows each granting scope
/// admits for them (<see cref="Restrict"/>).</item>
/// <item>Any other caller, on a read model without access rules, is <see cref="Standing.Refused"/>
/// (2.7.0's 403).</item>
/// <item>A read model without <c>requiredRole</c> has no role holders, so with access rules every
/// caller is restricted. Without access rules it is unchanged: open to every caller.</item>
/// </list></para>
///
/// <para><b>Failing closed.</b> The 2.7.0 posture stands for a read model that declares only
/// <c>requiredRole</c>: a host that never wired a role resolver gets an open route, as before. A read
/// model with access rules is different. If the host has no role resolver, the route cannot tell a
/// role holder from anyone else, so it refuses rather than serve every row.</para>
///
/// <para><b>Bound params.</b> <c>selfAccess.param</c> and a granting scope's param always name the
/// caller. Whatever value a request sends is ignored (<see cref="BindSelf"/>, <see cref="BindGrant"/>).
/// They narrow any caller, role holders included, so a manager can ask for only their own rows.</para>
///
/// <para>A caller whose subject id cannot be resolved (null or empty) gets no rows, not an error,
/// for the reason schema 3.2.0 gives: an empty list is what "you have none" looks like.</para>
/// </summary>
public static class ReadAccess
{
    /// <summary>A granting scope (<c>grantsAccess: true</c>), as columns: rows whose
    /// <c>LocalColumn</c> is among the <c>SelectColumn</c> values of <c>ViaTable</c> rows whose
    /// <c>MatchColumn</c> is the caller's subject id.</summary>
    public sealed record Grant(string Param, string ViaTable, string MatchColumn, string SelectColumn, string LocalColumn);

    /// <summary>A read model's access declarations, as columns. <c>SubjectColumn</c> and
    /// <c>SelfParam</c> come from <c>selfAccess</c>; either may be null.</summary>
    public sealed record Policy(string[]? RequiredRole, string? SubjectColumn, string? SelfParam, Grant[] Grants)
    {
        /// <summary>Whether the read model declares <c>selfAccess</c> or a granting scope.</summary>
        public bool HasRules => SubjectColumn is not null || Grants.Length > 0;

        /// <summary>Whether <paramref name="param"/> is bound to the caller rather than read from the request.</summary>
        public bool IsBound(string param) => param == SelfParam || Grants.Any(g => g.Param == param);
    }

    public enum Standing
    {
        /// <summary>Every row; params narrow.</summary>
        Unrestricted,
        /// <summary>Only the caller's own rows and the rows granted to them; params narrow within that.</summary>
        Restricted,
        /// <summary>403.</summary>
        Refused,
    }

    /// <summary>Where a caller stands. <paramref name="roleResolverWired"/> is whether the host
    /// resolves roles at all; <paramref name="ownRole"/> is the caller's role when it does.</summary>
    public static Standing Decide(Policy policy, bool roleResolverWired, string? ownRole)
    {
        if (policy.RequiredRole is not { Length: > 0 } required)
            return policy.HasRules ? Standing.Restricted : Standing.Unrestricted;
        if (!roleResolverWired)
            return policy.HasRules ? Standing.Refused : Standing.Unrestricted;
        if (required.Contains(ownRole ?? "", StringComparer.OrdinalIgnoreCase))
            return Standing.Unrestricted;
        return policy.HasRules ? Standing.Restricted : Standing.Refused;
    }

    /// <summary>Adds the clause limiting a restricted caller to their own rows and their granted
    /// rows. Returns the next parameter index.</summary>
    public static int Restrict(Policy policy, string? subjectId, List<string> clauses, Dictionary<string, object?> parameters, int index)
    {
        if (string.IsNullOrEmpty(subjectId))
        {
            clauses.Add("1 = 0");
            return index;
        }
        var subject = $"@p{index++}";
        parameters[subject] = subjectId;
        var visible = new List<string>();
        if (policy.SubjectColumn is { } column)
            visible.Add($"{column} = {subject}");
        foreach (var grant in policy.Grants)
            visible.Add(GrantClause(grant, subject));
        clauses.Add("(" + string.Join(" OR ", visible) + ")");
        return index;
    }

    /// <summary>The <c>selfAccess.param</c> case: narrows any caller to their own rows. Returns the
    /// next parameter index.</summary>
    public static int BindSelf(Policy policy, string? subjectId, List<string> clauses, Dictionary<string, object?> parameters, int index)
    {
        var column = policy.SubjectColumn
            ?? throw new InvalidOperationException("selfAccess.param needs selfAccess.subjectField");
        if (string.IsNullOrEmpty(subjectId))
        {
            clauses.Add("1 = 0");
            return index;
        }
        var subject = $"@p{index++}";
        parameters[subject] = subjectId;
        clauses.Add($"{column} = {subject}");
        return index;
    }

    /// <summary>A granting scope's param: narrows any caller to the rows that scope admits for
    /// them. Returns the next parameter index.</summary>
    public static int BindGrant(Grant grant, string? subjectId, List<string> clauses, Dictionary<string, object?> parameters, int index)
    {
        if (string.IsNullOrEmpty(subjectId))
        {
            clauses.Add("1 = 0");
            return index;
        }
        var subject = $"@p{index++}";
        parameters[subject] = subjectId;
        clauses.Add(GrantClause(grant, subject));
        return index;
    }

    private static string GrantClause(Grant grant, string subject) =>
        $"{grant.LocalColumn} IN (SELECT {grant.SelectColumn} FROM {grant.ViaTable} WHERE {grant.MatchColumn} = {subject})";
}
