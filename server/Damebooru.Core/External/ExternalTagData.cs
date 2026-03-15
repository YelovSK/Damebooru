using Damebooru.Core.Entities;

namespace Damebooru.Core.External;

public sealed record ExternalTagData(string Name, TagCategoryKind Category);
