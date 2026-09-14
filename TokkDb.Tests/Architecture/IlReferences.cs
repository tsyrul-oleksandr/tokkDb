using System.Reflection;
using System.Reflection.Emit;

namespace TokkDb.Tests.Architecture;

//What a compiled method refers to: every method it calls, every field it touches and every
//member it takes a token of, read out of its IL. The architecture tests of HS-3 stand on this
//rather than on source text, so that a lambda, a local function or a generated closure is
//held to the same rule as the method it sits in.
internal static class IlReferences {
  private static readonly OpCode[] OneByte = new OpCode[256];
  private static readonly OpCode[] TwoByte = new OpCode[256];

  static IlReferences() {
    foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) {
      var opCode = (OpCode)field.GetValue(null)!;
      if (opCode.Size == 1) {
        OneByte[opCode.Value & 0xFF] = opCode;
      } else {
        TwoByte[opCode.Value & 0xFF] = opCode;
      }
    }
  }

  //Every method and constructor of every type of the assembly, nested and generated types
  //included, with the outermost type each one belongs to.
  public static IEnumerable<(Type Owner, MethodBase Method)> Methods(Assembly assembly) {
    const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
      | BindingFlags.Static | BindingFlags.DeclaredOnly;
    foreach (var type in assembly.GetTypes()) {
      var owner = type;
      while (owner.DeclaringType is not null) {
        owner = owner.DeclaringType;
      }
      foreach (var method in type.GetMethods(all)) {
        yield return (owner, method);
      }
      foreach (var constructor in type.GetConstructors(all)) {
        yield return (owner, constructor);
      }
    }
  }

  public static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method) {
    var body = method.GetMethodBody();
    if (body is null) {
      yield break;
    }
    var il = body.GetILAsByteArray();
    if (il is null) {
      yield break;
    }
    var module = method.Module;
    var typeArguments = method.DeclaringType is { IsGenericType: true } declaring
      ? declaring.GetGenericArguments()
      : null;
    var methodArguments = method is MethodInfo { IsGenericMethod: true } generic
      ? generic.GetGenericArguments()
      : null;
    var position = 0;
    while (position < il.Length) {
      OpCode opCode;
      if (il[position] == 0xFE) {
        opCode = TwoByte[il[position + 1]];
        position += 2;
      } else {
        opCode = OneByte[il[position]];
        position += 1;
      }
      switch (opCode.OperandType) {
        case OperandType.InlineNone:
          break;
        case OperandType.ShortInlineBrTarget:
        case OperandType.ShortInlineI:
        case OperandType.ShortInlineVar:
          position += 1;
          break;
        case OperandType.InlineVar:
          position += 2;
          break;
        case OperandType.InlineI8:
        case OperandType.InlineR:
          position += 8;
          break;
        case OperandType.InlineSwitch: {
          var count = BitConverter.ToInt32(il, position);
          position += 4 + 4 * count;
          break;
        }
        case OperandType.InlineMethod:
        case OperandType.InlineField:
        case OperandType.InlineTok: {
          var token = BitConverter.ToInt32(il, position);
          position += 4;
          MemberInfo? member = null;
          try {
            member = module.ResolveMember(token, typeArguments, methodArguments);
          } catch (ArgumentException) {
            //A token this method's generic context cannot resolve names nothing we check.
          }
          if (member is not null) {
            yield return member;
          }
          break;
        }
        default:
          position += 4;
          break;
      }
    }
  }
}
