// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.InteropServices;
using System.Text;

namespace Viewer.Shell;

/// <summary>
/// Reads, on Windows and before the engine has chosen anything, what the graphics driver offers each backend, so the viewer can hold both to the
/// hardware floor and pick one (<see cref="BackendChoice"/>). Vulkan: the API version the adapter reports through the system's Vulkan loader
/// (vulkan-1.dll; vkGetPhysicalDeviceProperties, preferring a discrete GPU as the engine does). Direct3D 12: the highest feature level the default
/// adapter supports (D3D12CreateDevice with no device requested). Anything that cannot be read is reported as missing, never guessed. The engine's
/// own report of the backend that actually starts is still the final check.
/// </summary>
public static class DeviceProbe
{
    /// <summary>The report, or null when this is not Windows or neither backend could be read at all.</summary>
    public static DeviceReport? Read()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        (string? vulkan, string? name) = ReadVulkan();
        string? d3d12 = ReadD3D12();
        return vulkan is null && d3d12 is null ? null : new DeviceReport(name ?? "default adapter", vulkan, d3d12);
    }

    // ---------------- Vulkan ----------------

    [DllImport("vulkan-1.dll")]
    private static extern int vkCreateInstance(IntPtr createInfo, IntPtr allocator, out IntPtr instance);

    [DllImport("vulkan-1.dll")]
    private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr devices);

    [DllImport("vulkan-1.dll")]
    private static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

    [DllImport("vulkan-1.dll")]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    private const int VkInstanceCreateInfoSize = 64;   // sType, pNext, flags, pApplicationInfo, layer and extension counts and lists (all zero here)
    private const int VkPropertiesSize = 1024;         // VkPhysicalDeviceProperties is 824 bytes; apiVersion at 0, deviceType at 16, deviceName at 20

    private static (string? Version, string? Name) ReadVulkan()
    {
        IntPtr info = IntPtr.Zero, props = IntPtr.Zero, list = IntPtr.Zero, instance = IntPtr.Zero;
        try
        {
            info = Marshal.AllocHGlobal(VkInstanceCreateInfoSize);
            for (int i = 0; i < VkInstanceCreateInfoSize; i += 8)
                Marshal.WriteInt64(info, i, 0);
            Marshal.WriteInt32(info, 0, 1);   // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
            if (vkCreateInstance(info, IntPtr.Zero, out instance) != 0 || instance == IntPtr.Zero)
                return (null, null);
            uint count = 0;
            if (vkEnumeratePhysicalDevices(instance, ref count, IntPtr.Zero) != 0 || count == 0)
                return (null, null);
            count = Math.Min(count, 16u);
            list = Marshal.AllocHGlobal((int)count * IntPtr.Size);
            if (vkEnumeratePhysicalDevices(instance, ref count, list) is not (0 or 5))   // 5: incomplete, the first ones are valid
                return (null, null);
            props = Marshal.AllocHGlobal(VkPropertiesSize);
            uint bestApi = 0;
            string? bestName = null;
            int bestRank = -1;
            for (int i = 0; i < count; i++)
            {
                for (int b = 0; b < VkPropertiesSize; b += 8)
                    Marshal.WriteInt64(props, b, 0);
                vkGetPhysicalDeviceProperties(Marshal.ReadIntPtr(list, i * IntPtr.Size), props);
                uint api = (uint)Marshal.ReadInt32(props, 0);
                int type = Marshal.ReadInt32(props, 16);
                int rank = type == 2 ? 2 : type == 1 ? 1 : 0;   // discrete, then integrated, then the rest
                if (rank > bestRank)
                {
                    bestRank = rank;
                    bestApi = api;
                    bestName = Marshal.PtrToStringAnsi(props + 20);
                }
            }
            return ($"{bestApi >> 22}.{(bestApi >> 12) & 0x3ff}.{bestApi & 0xfff}", bestName);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return (null, null);
        }
        finally
        {
            if (instance != IntPtr.Zero)
                vkDestroyInstance(instance, IntPtr.Zero);
            foreach (var p in new[] { info, props, list })
                if (p != IntPtr.Zero)
                    Marshal.FreeHGlobal(p);
        }
    }

    // ---------------- Direct3D 12 ----------------

    [DllImport("d3d12.dll")]
    private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, IntPtr device);

    private static readonly (int Level, string Text)[] Levels =
        [(0xC200, "12_2"), (0xC100, "12_1"), (0xC000, "12_0"), (0xB100, "11_1"), (0xB000, "11_0")];

    private static string? ReadD3D12()
    {
        try
        {
            var device = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");   // ID3D12Device
            foreach (var (level, text) in Levels)
                if (D3D12CreateDevice(IntPtr.Zero, level, ref device, IntPtr.Zero) >= 0)   // no device requested: S_FALSE says it could be made
                    return text;
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return null;
        }
    }
}
