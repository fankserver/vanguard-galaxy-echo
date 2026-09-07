using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace VGEcho.Tests;

/// <summary>
/// Regressions for the metadata SCAN that <see cref="PluginAssemblyShapeTests"/>
/// relies on. That scan carries a real conclusion — "no always-loaded VGEcho
/// member makes Mono resolve the API assembly" — so a scan blind to one token
/// class would turn the proof into a false pass.
///
/// <para>Each case is a synthetic in-memory module, not VGEcho's own source: a
/// fixture built from the production assembly could only ever confirm the shapes
/// that already exist there, never the ones the scan must catch if they appear.
/// A clean control proves the scan is not trivially positive.</para>
/// </summary>
public sealed class ShapeScanRegressionTests
{
    private const string ApiAssembly = "VGModAPI.Abstractions";

    private sealed class Synthetic : IDisposable
    {
        internal readonly AssemblyDefinition Assembly;
        internal readonly ModuleDefinition Module;
        internal readonly TypeReference ApiType;

        internal Synthetic()
        {
            Assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("SyntheticConsumer", new Version(1, 0)), "SyntheticConsumer", ModuleKind.Dll);
            Module = Assembly.MainModule;
            var scope = new AssemblyNameReference(ApiAssembly, new Version(0, 1, 9));
            Module.AssemblyReferences.Add(scope);
            ApiType = new TypeReference("VGModAPI", "TravelTransition", Module, scope);
        }

        internal TypeDefinition AddType(string name, TypeReference? baseType = null)
        {
            var type = new TypeDefinition("Synthetic", name,
                TypeAttributes.Public | TypeAttributes.Class, baseType ?? Module.TypeSystem.Object);
            Module.Types.Add(type);
            return type;
        }

        internal static MethodDefinition AddBody(TypeDefinition owner, string name, Action<ILProcessor> emit)
        {
            var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static,
                owner.Module.TypeSystem.Void);
            var il = method.Body.GetILProcessor();
            emit(il);
            il.Emit(OpCodes.Ret);
            owner.Methods.Add(method);
            return method;
        }

        public void Dispose() => Assembly.Dispose();
    }

    private static string[] Scan(TypeDefinition type) => PluginAssemblyShapeTests.ApiReferences(type).ToArray();

    /// <summary>A type is loaded together with its base type, before any of its
    /// method bodies are compiled — the earliest possible escape.</summary>
    [Fact]
    public void TheScanCatchesAnApiBaseType()
    {
        using var synthetic = new Synthetic();
        var derived = synthetic.AddType("DerivesFromApi", synthetic.ApiType);
        Assert.Contains(Scan(derived), finding => finding.Contains("base") && finding.Contains("TravelTransition"));
    }

    /// <summary>Implemented interfaces load with the type for the same reason.</summary>
    [Fact]
    public void TheScanCatchesAnApiInterface()
    {
        using var synthetic = new Synthetic();
        var implementer = synthetic.AddType("ImplementsApi");
        implementer.Interfaces.Add(new InterfaceImplementation(
            new TypeReference("VGModAPI", "ITravelEvents", synthetic.Module, synthetic.ApiType.Scope)));
        Assert.Contains(Scan(implementer), finding => finding.Contains("implements") && finding.Contains("ITravelEvents"));
    }

    /// <summary>A generic method's type argument lives on the CALL SITE, so it
    /// never appears in the callee's own signature.</summary>
    [Fact]
    public void TheScanCatchesAnApiGenericMethodTypeArgument()
    {
        using var synthetic = new Synthetic();
        var helper = synthetic.AddType("Helper");
        var generic = new MethodDefinition("Handle", MethodAttributes.Public | MethodAttributes.Static,
            synthetic.Module.TypeSystem.Void);
        generic.GenericParameters.Add(new GenericParameter("T", generic));
        generic.Body.GetILProcessor().Emit(OpCodes.Ret);
        helper.Methods.Add(generic);

        var instantiated = new GenericInstanceMethod(generic);
        instantiated.GenericArguments.Add(synthetic.ApiType);
        var caller = synthetic.AddType("Caller");
        Synthetic.AddBody(caller, "Invoke", il => il.Emit(OpCodes.Call, instantiated));

        Assert.Contains(Scan(caller), finding => finding.Contains("TravelTransition"));
    }

    /// <summary>An API type wrapped in a generic instance still resolves it.</summary>
    [Fact]
    public void TheScanCatchesAnApiTypeInsideAGenericInstance()
    {
        using var synthetic = new Synthetic();
        var list = new TypeReference("System.Collections.Generic", "List`1", synthetic.Module,
            synthetic.Module.TypeSystem.CoreLibrary);
        list.GenericParameters.Add(new GenericParameter("T", list));
        var instance = new GenericInstanceType(list);
        instance.GenericArguments.Add(synthetic.ApiType);

        var holder = synthetic.AddType("HoldsGenericInstance");
        holder.Fields.Add(new FieldDefinition("facts", FieldAttributes.Private, instance));
        Assert.Contains(Scan(holder), finding => finding.Contains("TravelTransition"));
    }

    /// <summary>Arrays, by-refs and pointers hide the element type one level down.</summary>
    [Fact]
    public void TheScanCatchesAnApiTypeBehindAnArray()
    {
        using var synthetic = new Synthetic();
        var holder = synthetic.AddType("HoldsArray");
        holder.Fields.Add(new FieldDefinition("facts", FieldAttributes.Private, new ArrayType(synthetic.ApiType)));
        Assert.Contains(Scan(holder), finding => finding.Contains("TravelTransition"));
    }

    [Fact]
    public void TheScanCatchesAnApiTypeInAMethodSignatureFieldOrLocal()
    {
        using var synthetic = new Synthetic();
        var holder = synthetic.AddType("HoldsEverywhere");
        holder.Fields.Add(new FieldDefinition("fact", FieldAttributes.Private, synthetic.ApiType));
        var method = new MethodDefinition("Take", MethodAttributes.Public, synthetic.Module.TypeSystem.Void);
        method.Parameters.Add(new ParameterDefinition("fact", ParameterAttributes.None, synthetic.ApiType));
        method.Body.Variables.Add(new VariableDefinition(synthetic.ApiType));
        method.Body.GetILProcessor().Emit(OpCodes.Ret);
        holder.Methods.Add(method);

        var findings = Scan(holder);
        Assert.Contains(findings, finding => finding.Contains(".fact : "));
        Assert.Contains(findings, finding => finding.Contains("takes"));
        Assert.Contains(findings, finding => finding.Contains("local"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheScanChecksCatchTypesWithoutFlaggingApiFreeHandlers(bool fromApi)
    {
        using var synthetic = new Synthetic();
        var owner = synthetic.AddType("HandlesException");
        var method = new MethodDefinition("Handle", MethodAttributes.Public | MethodAttributes.Static,
            synthetic.Module.TypeSystem.Void);
        owner.Methods.Add(method);
        var il = method.Body.GetILProcessor();
        var start = Instruction.Create(OpCodes.Nop);
        var handlerStart = Instruction.Create(OpCodes.Pop);
        var end = Instruction.Create(OpCodes.Ret);
        il.Append(start);
        il.Emit(OpCodes.Leave, end);
        il.Append(handlerStart);
        il.Emit(OpCodes.Leave, end);
        il.Append(end);
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = start,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = end,
            CatchType = fromApi
                ? new TypeReference("VGModAPI", "SomeException", synthetic.Module, synthetic.ApiType.Scope)
                : synthetic.Module.ImportReference(typeof(Exception))
        });

        if (fromApi)
            Assert.Contains(Scan(owner), finding => finding.Contains("catches VGModAPI.SomeException"));
        else
            Assert.Empty(Scan(owner));
    }

    /// <summary>Control: a type touching none of it produces no findings, so the
    /// cases above are detections rather than a scan that always fires.</summary>
    [Fact]
    public void TheScanReportsNothingForAnApiFreeType()
    {
        using var synthetic = new Synthetic();
        var clean = synthetic.AddType("Clean");
        clean.Fields.Add(new FieldDefinition("count", FieldAttributes.Private, synthetic.Module.TypeSystem.Int32));
        Synthetic.AddBody(clean, "Nothing", _ => { });
        Assert.Empty(Scan(clean));
    }
}
