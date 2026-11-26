using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AotRuntime
{
    /// <summary>
    /// The exports of Unity's coreclr.dll that have no implementation here yet. Each logs its first calls so a run shows
    /// which ones the engine needs. When one gets a real implementation, delete its line.
    /// </summary>
    public static unsafe class Stubs
    {
        static readonly Dictionary<string, int> s_Calls = new Dictionary<string, int>();

        static nint Called(string name, nint a, nint b, nint c, nint d, nint result = 0)
        {
            int n;
            lock (s_Calls) { s_Calls.TryGetValue(name, out n); s_Calls[name] = ++n; }
            if (n <= 3 || n % 1000 == 0)
                Log.Line($"STUB {name}(0x{a:x}, 0x{b:x}, 0x{c:x}, 0x{d:x}) -> {result} [call {n}]");
            return result;
        }

        [UnmanagedCallersOnly(EntryPoint = "coreclr_execute_assembly", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_execute_assembly(nint a, nint b, nint c, nint d) => Called("coreclr_execute_assembly", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_image_get_custom_attribute_data", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_image_get_custom_attribute_data(nint a, nint b, nint c, nint d) => Called("coreclr_image_get_custom_attribute_data", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_set_error_writer", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_set_error_writer(nint a, nint b, nint c, nint d) => Called("coreclr_set_error_writer", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_shutdown", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_shutdown(nint a, nint b, nint c, nint d) => Called("coreclr_shutdown", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_shutdown_2", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_shutdown_2(nint a, nint b, nint c, nint d) => Called("coreclr_shutdown_2", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_gc_concurrent_mode", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_gc_concurrent_mode(nint a, nint b, nint c, nint d) => Called("coreclr_unity_gc_concurrent_mode", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_gc_stress_pause", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_gc_stress_pause(nint a, nint b, nint c, nint d) => Called("coreclr_unity_gc_stress_pause", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_get_stackframe_info_from_ip", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_get_stackframe_info_from_ip(nint a, nint b, nint c, nint d) => Called("coreclr_unity_get_stackframe_info_from_ip", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_allocate_struct", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_liveness_allocate_struct(nint a, nint b, nint c, nint d) => Called("coreclr_unity_liveness_allocate_struct", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_calculation_from_root", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_liveness_calculation_from_root(nint a, nint b, nint c, nint d) => Called("coreclr_unity_liveness_calculation_from_root", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_calculation_from_statics", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_liveness_calculation_from_statics(nint a, nint b, nint c, nint d) => Called("coreclr_unity_liveness_calculation_from_statics", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_finalize", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_liveness_finalize(nint a, nint b, nint c, nint d) => Called("coreclr_unity_liveness_finalize", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_free_struct", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_liveness_free_struct(nint a, nint b, nint c, nint d) => Called("coreclr_unity_liveness_free_struct", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_start_world", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_liveness_start_world(nint a, nint b, nint c, nint d) => Called("coreclr_unity_liveness_start_world", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_stop_world", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_liveness_stop_world(nint a, nint b, nint c, nint d) => Called("coreclr_unity_liveness_stop_world", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_profiler_assembly_load_context_get_loader_allocator_handle", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_profiler_assembly_load_context_get_loader_allocator_handle(nint a, nint b, nint c, nint d) => Called("coreclr_unity_profiler_assembly_load_context_get_loader_allocator_handle", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_profiler_class_get_assembly_load_context_handle", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_profiler_class_get_assembly_load_context_handle(nint a, nint b, nint c, nint d) => Called("coreclr_unity_profiler_class_get_assembly_load_context_handle", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_profiler_get_managed_assembly_load_context", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_profiler_get_managed_assembly_load_context(nint a, nint b, nint c, nint d) => Called("coreclr_unity_profiler_get_managed_assembly_load_context", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_profiler_loader_allocator_get_assembly_load_context_handle", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_profiler_loader_allocator_get_assembly_load_context_handle(nint a, nint b, nint c, nint d) => Called("coreclr_unity_profiler_loader_allocator_get_assembly_load_context_handle", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_profiler_register", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_profiler_register(nint a, nint b, nint c, nint d) => Called("coreclr_unity_profiler_register", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_set_on_fatal_error", CallConvs = new[] { typeof(CallConvCdecl) })] static nint coreclr_unity_set_on_fatal_error(nint a, nint b, nint c, nint d) => Called("coreclr_unity_set_on_fatal_error", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "mono_add_internal_call", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_add_internal_call(nint a, nint b, nint c, nint d) => Called("mono_add_internal_call", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "mono_assembly_get_assemblyref", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_assembly_get_assemblyref(nint a, nint b, nint c, nint d) => Called("mono_assembly_get_assemblyref", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "mono_class_get_type", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_class_get_type(nint a, nint b, nint c, nint d) => Called("mono_class_get_type", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "mono_gchandle_get_target_v2", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_gchandle_get_target_v2(nint a, nint b, nint c, nint d) => Called("mono_gchandle_get_target_v2", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "mono_image_get_table_rows", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_image_get_table_rows(nint a, nint b, nint c, nint d) => Called("mono_image_get_table_rows", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "mono_method_get_last_managed", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_method_get_last_managed(nint a, nint b, nint c, nint d) => Called("mono_method_get_last_managed", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "mono_runtime_invoke", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_runtime_invoke(nint a, nint b, nint c, nint d) => Called("mono_runtime_invoke", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "mono_thread_attach", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_thread_attach(nint a, nint b, nint c, nint d) => Called("mono_thread_attach", a, b, c, d, 1);
        [UnmanagedCallersOnly(EntryPoint = "mono_thread_current", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_thread_current(nint a, nint b, nint c, nint d) => Called("mono_thread_current", a, b, c, d, 1);
        [UnmanagedCallersOnly(EntryPoint = "mono_thread_detach", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_thread_detach(nint a, nint b, nint c, nint d) => Called("mono_thread_detach", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "mono_type_get_object", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_type_get_object(nint a, nint b, nint c, nint d) => Called("mono_type_get_object", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "mono_type_get_type", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_type_get_type(nint a, nint b, nint c, nint d) => Called("mono_type_get_type", a, b, c, d);
        [UnmanagedCallersOnly(EntryPoint = "mono_unity_register_path_remapper", CallConvs = new[] { typeof(CallConvCdecl) })] static nint mono_unity_register_path_remapper(nint a, nint b, nint c, nint d) => Called("mono_unity_register_path_remapper", a, b, c, d);
    }
}
