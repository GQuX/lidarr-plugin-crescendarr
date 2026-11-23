// M25: independent, mechanical proof that an AUDIT-ONLY Crescendarr assembly cannot mutate a Lidarr library.
// Reads the compiled DLL's metadata and IL (System.Reflection.Metadata; the assembly is never loaded or executed) and fails when:
//   1. it defines any writer command type (CrescendarrImport/Recover/Approve/Propose/Cycle...) or the writer Connect provider;
//   2. it references a Lidarr type or member that mutates media, lyrics, track links, settings or download clients;
//   3. any method outside the allow-listed, barrier-protected sites calls a filesystem mutator (File.Move/Delete/Copy/Write*,
//      Directory.Delete/Move, FileStream construction, File.Open* for writing, SetLastWriteTime*);
//   4. a barrier-protected site does not call AuditBarrier.RequireStatePath.
// Usage: audit-scan <Lidarr.Plugin.Crescendarr.dll>    Exit code 0 = no violation.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class Program
{
    private static readonly string[] ForbiddenTypeDefinitions =
    {
        "CrescendarrImportCommand", "CrescendarrRecoverCommand", "CrescendarrApproveCommand", "CrescendarrProposeCommand",
        "CrescendarrCycleCommand", "CrescendarrPlanCommand", "CrescendarrConnect", "CrescendarrImportExecutor", "ImportBoundary",
    };

    // Lidarr services whose ONLY purpose is changing media, lyrics, links or recycle state: any reference at all fails.
    private static readonly string[] ForbiddenHostTypes =
    {
        "NzbDrone.Core.MediaFiles.TrackImport.IImportApprovedTracks", "NzbDrone.Core.MediaFiles.IUpgradeMediaFiles",
        "NzbDrone.Core.MediaFiles.IRecycleBinProvider", "NzbDrone.Core.MediaFiles.IMoveTrackFiles", "NzbDrone.Core.MediaFiles.IDeleteMediaFiles",
        "NzbDrone.Core.MediaFiles.IMediaFileTableCleanupService", "NzbDrone.Core.Extras.IExtraService", "NzbDrone.Core.MediaFiles.IRenameTrackFileService",
        "NzbDrone.Core.MediaFiles.IAudioTagService", "NzbDrone.Common.Disk.IDiskTransferService", "NzbDrone.Common.Disk.IDiskProvider",
    };

    // Member-name prefixes that mutate state when called on ANY Lidarr type.
    private static readonly string[] MutatingPrefixes =
        { "Add", "Insert", "Update", "Upsert", "Delete", "Remove", "Import", "Move", "Recycle", "Clean", "Rename", "Retag", "Write", "Save", "Purge", "SetFileId", "Set" };

    // Lidarr DTOs the read-only adapter legitimately initialises (object initialisers compile to set_*).
    private static readonly string[] SetterAllowedTypes =
        { "ImportDecisionMakerConfig", "ImportDecisionMakerInfo", "IdentificationOverrides", "ValidationFailure", "NzbDroneValidationResult" };

    // Commands and events: constructing/pushing the audit's own command is the only queue use.
    private static readonly string[] AllowedLidarrMutatorMembers = { "Push" };

    private static readonly (string Type, string Member)[] FileMutators =
    {
        ("System.IO.File", "Move"), ("System.IO.File", "Delete"), ("System.IO.File", "Copy"), ("System.IO.File", "Replace"),
        ("System.IO.File", "WriteAllBytes"), ("System.IO.File", "WriteAllText"), ("System.IO.File", "WriteAllLines"), ("System.IO.File", "AppendAllText"),
        ("System.IO.File", "AppendAllLines"), ("System.IO.File", "Create"), ("System.IO.File", "CreateText"), ("System.IO.File", "OpenWrite"),
        ("System.IO.File", "Open"), ("System.IO.File", "SetLastWriteTime"), ("System.IO.File", "SetLastWriteTimeUtc"), ("System.IO.File", "SetAttributes"),
        ("System.IO.File", "CreateSymbolicLink"), ("System.IO.Directory", "Delete"), ("System.IO.Directory", "Move"), ("System.IO.Directory", "CreateDirectory"),
        ("System.IO.Directory", "CreateSymbolicLink"), ("System.IO.FileInfo", "Delete"), ("System.IO.FileInfo", "MoveTo"), ("System.IO.FileInfo", "CopyTo"),
        ("System.IO.FileInfo", "set_LastWriteTimeUtc"), ("System.IO.DirectoryInfo", "Delete"), ("System.IO.FileStream", ".ctor"),
    };

    // Barrier-protected sites: each must itself call AuditBarrier.RequireStatePath before mutating.
    private static readonly (string Type, string Method)[] BarrierSites =
    {
        ("NzbDrone.Core.Plugins.Crescendarr.Disk", "WriteAtomically"),
        ("NzbDrone.Core.Plugins.Crescendarr.Disk", "WriteProbeProblem"),
        ("NzbDrone.Core.Plugins.Crescendarr.CrescendarrAuditExecutor", "Read"),
    };

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("usage: audit-scan <dll>"); return 2; }
        using var stream = File.OpenRead(args[0]);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var violations = new List<string>();
        var hostMembers = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var handle in md.TypeDefinitions)
        {
            var name = md.GetString(md.GetTypeDefinition(handle).Name);
            if (ForbiddenTypeDefinitions.Contains(name)) violations.Add($"defines writer type {name}");
        }

        foreach (var handle in md.TypeReferences)
        {
            var full = TypeRefName(md, handle);
            if (ForbiddenHostTypes.Contains(full)) violations.Add($"references library-mutation service {full}");
        }

        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);
            var owner = ParentName(md, member.Parent);
            var name = md.GetString(member.Name);
            if (!owner.StartsWith("NzbDrone.", StringComparison.Ordinal) && !owner.StartsWith("Lidarr.", StringComparison.Ordinal)) continue;
            hostMembers.Add(owner + "::" + name);
            var shortOwner = owner[(owner.LastIndexOf('.') + 1)..];
            if (AllowedLidarrMutatorMembers.Contains(name)) continue;
            if (name.StartsWith("set_", StringComparison.Ordinal) && SetterAllowedTypes.Any(t => shortOwner.StartsWith(t, StringComparison.Ordinal))) continue;
            var bare = name.StartsWith("set_", StringComparison.Ordinal) ? "Set" : name;
            if (MutatingPrefixes.Any(p => bare.StartsWith(p, StringComparison.Ordinal)))
                violations.Add($"calls mutating Lidarr member {owner}::{name}");
        }

        // IL: who calls a filesystem mutator, and do barrier sites call the barrier?
        var barrierCalled = new HashSet<string>(StringComparer.Ordinal);
        var mutatorCallers = new List<(string Caller, string Callee)>();
        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            var typeName = OuterTypeName(md, typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;
                var caller = typeName + "::" + LogicalMethodName(md.GetString(method.Name));
                foreach (var callee in Calls(md, pe.GetMethodBody(method.RelativeVirtualAddress)))
                {
                    if (callee == "NzbDrone.Core.Plugins.Crescendarr.AuditBarrier::RequireStatePath") barrierCalled.Add(caller);
                    var split = callee.Split("::");
                    if (FileMutators.Any(m => m.Type == split[0] && (split[1] == m.Member || split[1].StartsWith(m.Member + "`", StringComparison.Ordinal))))
                        mutatorCallers.Add((caller, callee));
                }
            }
        }

        foreach (var (caller, callee) in mutatorCallers.Distinct())
        {
            var site = BarrierSites.FirstOrDefault(s => caller == s.Type + "::" + s.Method);
            Console.WriteLine($"FILE_MUTATOR {caller} -> {callee}{(site.Type == null ? "  (NOT ALLOWED)" : "  (barrier site)")}");
            if (site.Type == null) violations.Add($"{caller} calls filesystem mutator {callee} outside the barrier-protected sites");
        }

        foreach (var (type, method) in BarrierSites)
        {
            var site = type + "::" + method;
            if (mutatorCallers.Any(c => c.Caller == site) && !barrierCalled.Contains(site))
                violations.Add($"{site} mutates the filesystem without calling AuditBarrier.RequireStatePath");
        }

        Console.WriteLine($"HOST_MEMBERS {hostMembers.Count} Lidarr members referenced:");
        foreach (var m in hostMembers) Console.WriteLine("  " + m);
        foreach (var v in violations) Console.WriteLine("VIOLATION " + v);
        Console.WriteLine(violations.Count == 0 ? "AUDIT_SCAN_OK no library-mutation capability found" : $"AUDIT_SCAN_FAILED {violations.Count} violation(s)");
        return violations.Count == 0 ? 0 : 1;
    }

    // Lambdas and local functions compile to "<Read>b__3_0" / "<WriteAtomically>g__X|4_0": attribute them to their outer method.
    private static string LogicalMethodName(string name)
    {
        if (name.StartsWith('<'))
        {
            var close = name.IndexOf('>');
            if (close > 1) return name[1..close];
        }

        return name;
    }

    // Closure classes nested in a type ("<>c__DisplayClass4_0") are attributed to the outermost declaring type.
    private static string OuterTypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        while (type.IsNested)
        {
            handle = type.GetDeclaringType();
            type = md.GetTypeDefinition(handle);
        }

        var ns = md.GetString(type.Namespace);
        return (ns.Length > 0 ? ns + "." : string.Empty) + md.GetString(type.Name);
    }

    private static IEnumerable<string> Calls(MetadataReader md, MethodBodyBlock body)
    {
        var reader = body.GetILReader();
        while (reader.RemainingBytes > 0)
        {
            short value = reader.ReadByte();
            if (value == 0xFE) value = unchecked((short)(0xFE00 | reader.ReadByte()));
            if (!OpCodesByValue.TryGetValue(value, out var op)) yield break;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: reader.ReadByte(); break;
                case OperandType.InlineVar: reader.ReadInt16(); break;
                case OperandType.InlineI8:
                case OperandType.InlineR: reader.ReadInt64(); break;
                case OperandType.InlineSwitch:
                    var count = reader.ReadInt32();
                    for (var i = 0; i < count; i++) reader.ReadInt32();
                    break;
                case OperandType.InlineMethod:
                    var token = reader.ReadInt32();
                    if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
                        yield return MethodName(md, MetadataTokens.EntityHandle(token));
                    break;
                default: reader.ReadInt32(); break;
            }
        }
    }

    private static string MethodName(MetadataReader md, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var def = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                return OuterTypeName(md, def.GetDeclaringType()) + "::" + md.GetString(def.Name);
            case HandleKind.MemberReference:
                var member = md.GetMemberReference((MemberReferenceHandle)handle);
                return ParentName(md, member.Parent) + "::" + md.GetString(member.Name);
            case HandleKind.MethodSpecification:
                return MethodName(md, md.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
            default:
                return "?::?";
        }
    }

    private static string ParentName(MetadataReader md, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => TypeRefName(md, (TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => OuterTypeName(md, (TypeDefinitionHandle)parent),
        HandleKind.TypeSpecification => SpecName(md, (TypeSpecificationHandle)parent),
        HandleKind.MethodDefinition => "method",
        _ => "?",
    };

    // Generic instantiations (e.g. IExtraFileService<LyricFile>): report the generic type's name.
    private static string SpecName(MetadataReader md, TypeSpecificationHandle handle)
    {
        var blob = md.GetBlobReader(md.GetTypeSpecification(handle).Signature);
        var kind = blob.ReadSignatureTypeCode();
        if (kind != SignatureTypeCode.GenericTypeInstance) return "?spec";
        blob.ReadSignatureTypeCode();
        var generic = blob.ReadTypeHandle();
        return generic.Kind == HandleKind.TypeReference ? TypeRefName(md, (TypeReferenceHandle)generic)
            : generic.Kind == HandleKind.TypeDefinition ? OuterTypeName(md, (TypeDefinitionHandle)generic) : "?spec";
    }

    private static string TypeRefName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        var name = md.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference) return TypeRefName(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;
        var ns = md.GetString(type.Namespace);
        return (ns.Length > 0 ? ns + "." : string.Empty) + name;
    }
}
