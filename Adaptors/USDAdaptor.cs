using Fleck;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class USDAdaptor : Adaptor
    {
        // reference to connected socket
        private readonly IWebSocketConnection? socket;

        [MemberNotNullWhen(true, nameof(socket))]
        protected override bool IsSendReady => socket != null && socket.IsAvailable;

        public USDAdaptor(WorldModel worldModel, IWebSocketConnection? socket, string type, int sendIntervalMs) : base(worldModel, sendIntervalMs)
        {
            this.socket = socket;

            if (socket != null)
                Id = GenerateId(type, socket.ConnectionInfo.ClientIpAddress, (uint)socket.ConnectionInfo.ClientPort);
        }

        protected override async Task Send(CancellationToken token)
        {
            // if can't send, return and wait for next interval to retry
            if (!IsSendReady)
                return;

            WorldUpdate update = worldModel.GetWorldModel(Id);

            StringBuilder sb = new StringBuilder();
            WriteUsd(sb, update);

            await socket.Send(sb.ToString());
        }

        public override void Receive(JsonElement msgRoot) { }   // won't receive anything from this adaptor type

        // ── Top-level document ──────────────────────────────────────────────────

        private static void WriteUsd(StringBuilder sb, WorldUpdate scene)
        {
            sb.AppendLine("#usda 1.0");
            sb.AppendLine("(");
            sb.AppendLine("    defaultPrim = \"Scene\"");
            sb.AppendLine("    upAxis = \"Y\"");
            sb.AppendLine("    metersPerUnit = 1.0");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("def Xform \"Scene\"");
            sb.AppendLine("{");

            // Objects scope
            if (scene.objects.Count > 0)
            {
                sb.AppendLine("    def Scope \"Objects\"");
                sb.AppendLine("    {");
                foreach (var obj in scene.objects)
                    WriteObject(sb, obj, indent: 2);
                sb.AppendLine("    }");
                sb.AppendLine();
            }

            // Users scope
            if (scene.users.Count > 0)
            {
                sb.AppendLine("    def Scope \"Users\"");
                sb.AppendLine("    {");
                foreach (var user in scene.users)
                    WriteUser(sb, user, indent: 2);
                sb.AppendLine("    }");
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        // ── Object prim ─────────────────────────────────────────────────────────

        private static void WriteObject(StringBuilder sb, WorldObject obj, int indent)
        {
            string pad = Pad(indent);
            string pad2 = Pad(indent + 1);
            string primName = SanitizeName(string.IsNullOrWhiteSpace(obj.name) ? obj.id : obj.name);

            sb.AppendLine($"{pad}def Xform \"{primName}\" (");
            sb.AppendLine($"{pad2}customData = {{");
            sb.AppendLine($"{pad2}    string id   = \"{Escape(obj.id)}\"");
            sb.AppendLine($"{pad2}    string tag  = \"{Escape(obj.tag)}\"");
            sb.AppendLine($"{pad2}    string home = \"{Escape(obj.home)}\"");
            sb.AppendLine($"{pad2}    string name = \"{Escape(obj.name)}\"");
            sb.AppendLine($"{pad2}}}");
            sb.AppendLine($"{pad})");
            sb.AppendLine($"{pad}{{");

            WriteTransformOps(sb, obj.transform, indent + 1);

            // Built-in boolean variables
            sb.AppendLine($"{pad2}# built-in variables");
            sb.AppendLine($"{pad2}custom bool live    = true");
            sb.AppendLine($"{pad2}custom bool visible = true");

            // Data variables
            foreach (var dv in obj.data)
            {
                // Override live/visible if explicitly set in data
                if (dv.name is "live" or "visible")
                {
                    sb.Replace(
                        $"{pad2}custom bool {dv.name,-7} = {(dv.name == "live" ? "true" : "true")}",
                        $"{pad2}custom bool {dv.name,-7} = {FormatValue(dv.value, "bool")}"
                    );
                    continue;
                }

                string usdType = MapType(dv.type);
                string usdVal = FormatValue(dv.value, dv.type);
                sb.AppendLine($"{pad2}custom {usdType} {SanitizeName(dv.name)} = {usdVal}");
            }

            sb.AppendLine($"{pad}}}");
            sb.AppendLine();
        }

        // ── User prim ───────────────────────────────────────────────────────────

        private static void WriteUser(StringBuilder sb, UserData user, int indent)
        {
            string pad = Pad(indent);
            string pad2 = Pad(indent + 1);
            string pad3 = Pad(indent + 2);
            string primName = SanitizeName(string.IsNullOrWhiteSpace(user.name) ? user.id : user.name);

            sb.AppendLine($"{pad}def Xform \"{primName}\" (");
            sb.AppendLine($"{pad2}customData = {{");
            sb.AppendLine($"{pad2}    string id   = \"{Escape(user.id)}\"");
            sb.AppendLine($"{pad2}    string home = \"{Escape(user.home)}\"");
            sb.AppendLine($"{pad2}    string name = \"{Escape(user.name)}\"");
            sb.AppendLine($"{pad2}}}");
            sb.AppendLine($"{pad})");
            sb.AppendLine($"{pad}{{");

            // Skeleton scope for bones
            if (user.boneNames.Length > 0)
            {
                sb.AppendLine($"{pad2}def Skeleton \"Skeleton\"");
                sb.AppendLine($"{pad2}{{");

                // Joint names array
                sb.AppendLine($"{pad3}uniform token[] joints = [");
                for (int i = 0; i < user.boneNames.Length; i++)
                {
                    string comma = i < user.boneNames.Length- 1 ? "," : "";
                    sb.AppendLine($"{pad3}    \"{Escape(user.boneNames[i])}\"{comma}");
                }
                sb.AppendLine($"{pad3}]");
                sb.AppendLine();

                // Rest transforms — position as translation, rotation as quaternion, scale
                sb.AppendLine($"{pad3}matrix4d[] restTransforms = [");
                for (int i = 0; i < user.boneTransforms.Length; i++)
                {
                    var t = user.boneTransforms[i];
                    string m = TransformToMatrix4d(t);
                    string comma = i < user.boneTransforms.Length - 1 ? "," : "";
                    sb.AppendLine($"{pad3}    {m}{comma}  # {(i < user.boneNames.Length ? user.boneNames[i] : i)}");
                }
                sb.AppendLine($"{pad3}]");

                sb.AppendLine($"{pad2}}}");
            }

            sb.AppendLine($"{pad}}}");
            sb.AppendLine();
        }

        // ── Transform ops ───────────────────────────────────────────────────────

        private static void WriteTransformOps(StringBuilder sb, Transform t, int indent)
        {
            string pad = Pad(indent);
            sb.AppendLine($"{pad}double3 xformOp:translate = ({F(t.position.X)}, {F(t.position.Y)}, {F(t.position.Z)})");
            sb.AppendLine($"{pad}quatf   xformOp:orient    = ({F(t.rotation.W)}, {F(t.rotation.X)}, {F(t.rotation.Y)}, {F(t.rotation.Z)})");
            sb.AppendLine($"{pad}float3  xformOp:scale     = ({F(t.scale.X)}, {F(t.scale.Y)}, {F(t.scale.Z)})");
            sb.AppendLine($"{pad}uniform token[] xformOpOrder = [\"xformOp:translate\", \"xformOp:orient\", \"xformOp:scale\"]");
            sb.AppendLine();
        }

        // ── Bone transforms → 4×4 matrix ────────────────────────────────────────
        // Converts TRS into a column-major 4×4 matrix (USD convention)

        private static string TransformToMatrix4d(Transform t)
        {
            float qx = t.rotation.X, qy = t.rotation.Y, qz = t.rotation.Z, qw = t.rotation.W;
            float sx = t.scale.X, sy = t.scale.Y, sz = t.scale.Z;

            // Rotation matrix from quaternion
            float r00 = (1 - 2 * (qy * qy + qz * qz)) * sx;
            float r01 = (2 * (qx * qy + qz * qw)) * sx;
            float r02 = (2 * (qx * qz - qy * qw)) * sx;

            float r10 = (2 * (qx * qy - qz * qw)) * sy;
            float r11 = (1 - 2 * (qx * qx + qz * qz)) * sy;
            float r12 = (2 * (qy * qz + qx * qw)) * sy;

            float r20 = (2 * (qx * qz + qy * qw)) * sz;
            float r21 = (2 * (qy * qz - qx * qw)) * sz;
            float r22 = (1 - 2 * (qx * qx + qy * qy)) * sz;

            float tx = t.position.X, ty = t.position.Y, tz = t.position.Z;

            return $"( ({F(r00)},{F(r01)},{F(r02)},0), ({F(r10)},{F(r11)},{F(r12)},0), ({F(r20)},{F(r21)},{F(r22)},0), ({F(tx)},{F(ty)},{F(tz)},1) )";
        }

        // ── Type mapping ─────────────────────────────────────────────────────────

        private static string MapType(string jsonType) => jsonType.ToLowerInvariant() switch
        {
            "string" => "string",
            "float" or "number" => "float",
            "double" => "double",
            "int" or "integer" => "int",
            "bool" or "boolean" => "bool",
            "vector2" => "float2",
            "vector3" => "float3",
            "vector4" => "float4",
            _ => "string"   // fallback: stringify unknowns
        };

        private static string FormatValue(JsonElement el, string hintType)
        {
            string h = hintType.ToLowerInvariant();

            if (h is "string")
                return $"\"{Escape(el.GetString() ?? el.ToString())}\"";

            if (h is "bool" or "boolean")
            {
                if (el.ValueKind == JsonValueKind.True) return "true";
                if (el.ValueKind == JsonValueKind.False) return "false";
                return $"\"{Escape(el.ToString())}\"";
            }

            if (h is "float" or "double" or "number")
            {
                if (el.TryGetDouble(out double d)) return F((float)d);
                return el.ToString();
            }

            if (h is "int" or "integer")
            {
                if (el.TryGetInt64(out long l)) return l.ToString();
                return el.ToString();
            }

            // Fallback: emit raw JSON value or quoted string
            return el.ValueKind == JsonValueKind.String
                ? $"\"{Escape(el.GetString() ?? "")}\""
                : el.ToString();
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        // Indentation: 4 spaces per level
        private static string Pad(int level) => new string(' ', level * 4);

        // Format float consistently, avoid scientific notation in USD
        private static string F(float v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

        // Escape backslashes and double-quotes inside USD string literals
        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // USD prim names must start with a letter/underscore and contain only [A-Za-z0-9_]
        private static string SanitizeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "_unnamed";
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }
    }
}
