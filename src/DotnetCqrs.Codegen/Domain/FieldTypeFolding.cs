namespace DotnetCqrs.Codegen.Domain;

/// <summary>Folds the schema's ten <c>fieldType</c> values onto the domain's five —
/// ports pocketcqrs's <c>emschema.FoldType</c>/<c>FoldNote</c>. Takes a
/// <see cref="Model.Field"/> (the schema-shaped field, with cardinality/subfields) and
/// produces the folded type string a <see cref="Field"/> (the domain-shaped field)
/// carries.</summary>
public static class FieldTypeFolding
{
    /// <summary>Ten values fold onto five, deliberately and deliberately documented
    /// (see <see cref="Note"/>). A list of anything, or a field with subfields, both
    /// become <c>json</c> — a column can hold neither.</summary>
    public static string Fold(Model.Field field)
    {
        if (field.Cardinality == "list" || field.Subfields is { Count: > 0 })
            return "json";

        return field.Type switch
        {
            "string" or "uuid" => "text",
            "boolean" => "bool",
            "integer" or "long" or "decimal" or "double" => "number",
            "date" or "dateTime" => "date",
            "custom" => "json",
            // an unknown type is not guessed at: text is the safe carrier, and the
            // caller reports the substitution rather than hiding it (see Note)
            _ => "text",
        };
    }

    /// <summary>Explains a fold that lost something, or null when it was faithful. A
    /// fold nobody is told about is the silent wrong-doing this is here to avoid.</summary>
    public static string? Note(string owner, Model.Field field)
    {
        if (field.Cardinality == "list")
            return $"{owner}.{field.Name} is a list of {field.Type} and becomes a json column";
        if (field.Subfields is { Count: > 0 })
            return $"{owner}.{field.Name} has subfields and becomes a json column";
        if (field.Type == "uuid")
            return $"{owner}.{field.Name} is a uuid and becomes text (no uuid column type here)";
        if (field.Type is "long" or "decimal" or "double" or "integer")
            return $"{owner}.{field.Name} is {field.Type} and becomes number (one numeric column type here)";
        if (field.Type == "dateTime")
            return $"{owner}.{field.Name} is dateTime and becomes date";
        if (field.Type == "custom")
            return $"{owner}.{field.Name} has a custom type and becomes a json column";
        if (Fold(field) == "text" && field.Type != "string")
            return $"{owner}.{field.Name} has unknown type \"{field.Type}\" and is carried as text";
        return null;
    }
}
