using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D_FEATURE_LEVEL;
using static TerraFX.Interop.DirectX.D3D12_COMMAND_LIST_TYPE;
using static TerraFX.Interop.DirectX.D3D12_DESCRIPTOR_HEAP_TYPE;
using static TerraFX.Interop.DirectX.D3D12_FENCE_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_HEAP_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_HEAP_TYPE;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_BARRIER_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_STATES;
using static TerraFX.Interop.DirectX.D3D12_TEXTURE_LAYOUT;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.Windows.PresentStatisticsKind;
using static TerraFX.Interop.Windows.Windows;

namespace PlayMap.Bench.Present;

/// <summary>M3: the Windows 11 composition swapchain (presentation.h) asked to take Direct3D 12 buffers and present at a target
/// time. Each ask is written to the record with what it answered — <c>CreatePresentationFactory</c> given the Direct3D 12 device and
/// then its direct queue, <c>IsPresentationSupported</c> and <c>…WithIndependentFlip</c>, <c>AddBufferFromResource</c> with two
/// shareable Direct3D 12 textures — and only when every one says yes does it present: a borderless window over the display carries
/// the presentation surface through DirectComposition, each frame's buffer is cleared black (the quad white on flash frames) and
/// presented with a target time two refreshes ahead, and the present ids and statuses the manager reports are kept. A no at any ask
/// ends the path there: that answer is the record (L4).</summary>
[SupportedOSPlatform("windows10.0.22000")]
internal sealed unsafe partial class CompositionPath : IDisposable
{
    private const int Buffers = 2;
    private const uint AllSubresources = 0xFFFFFFFF; // D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES
    private const uint CompositionObjectAllAccess = 0x0003; // COMPOSITIONOBJECT_ALL_ACCESS (dcomp.h): read | write

    private readonly RECT _quad;
    private readonly nint[] _textures = new nint[Buffers];
    private readonly nint[] _buffers = new nint[Buffers];
    private readonly HANDLE[] _available = new HANDLE[Buffers];
    private readonly ulong _refresh100ns;
    private readonly BenchWindow? _window;
    private ID3D12Device* _device;
    private ID3D12CommandQueue* _queue;
    private ID3D12CommandAllocator* _allocator;
    private ID3D12GraphicsCommandList* _list;
    private ID3D12DescriptorHeap* _rtvHeap;
    private ID3D12Fence* _fence;
    private IPresentationFactory* _factory;
    private IPresentationManager* _manager;
    private IPresentationSurface* _surface;
    private IDCompositionDevice* _dcomp;
    private IDCompositionTarget* _target;
    private IDCompositionVisual* _visual;
    private IUnknown* _content;
    private readonly HANDLE _surfaceHandle;
    private readonly HANDLE _event;
    private uint _rtvStep;
    private ulong _signalled;

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void QueryInterruptTimePrecise(out ulong interruptTime100ns);

    public CompositionPath(BenchDisplay display, bool warp, int refreshHz, PresentRecord record)
    {
        _quad = display.Quad;
        _refresh100ns = (ulong)(10_000_000 / Math.Max(1, refreshHz));
        _event = CreateEventW(null, false, false, null);
        IDXGIFactory6* dxgi = null;
        IDXGIAdapter1* adapter = null;
        try
        {
            Need(CreateDXGIFactory2(0, __uuidof<IDXGIFactory6>(), (void**)&dxgi), "CreateDXGIFactory2");
            if (warp)
                Need(dxgi->EnumWarpAdapter(__uuidof<IDXGIAdapter1>(), (void**)&adapter), "EnumWarpAdapter");
            else
                Need(dxgi->EnumAdapters1(display.Adapter, &adapter), "EnumAdapters1");
            ID3D12Device* device = null;
            Need(D3D12CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device), "D3D12CreateDevice");
            _device = device;
            var queueDesc = new D3D12_COMMAND_QUEUE_DESC { Type = D3D12_COMMAND_LIST_TYPE_DIRECT };
            ID3D12CommandQueue* queue = null;
            Need(_device->CreateCommandQueue(&queueDesc, __uuidof<ID3D12CommandQueue>(), (void**)&queue), "CreateCommandQueue");
            _queue = queue;

            // The asks: a factory on the Direct3D 12 device, then on its queue.
            IPresentationFactory* factory = null;
            var onDevice = CreatePresentationFactory((IUnknown*)_device, __uuidof<IPresentationFactory>(), (void**)&factory);
            record.Note(Words("composition: CreatePresentationFactory(the Direct3D 12 device)", onDevice));
            if (FAILED(onDevice))
            {
                var onQueue = CreatePresentationFactory((IUnknown*)_queue, __uuidof<IPresentationFactory>(), (void**)&factory);
                record.Note(Words("composition: CreatePresentationFactory(its direct queue)", onQueue));
                if (FAILED(onQueue))
                    return;
            }
            _factory = factory;
            var supported = _factory->IsPresentationSupported() != 0;
            var iflip = _factory->IsPresentationSupportedWithIndependentFlip() != 0;
            record.Note($"composition: IsPresentationSupported {(supported ? "yes" : "no")}, with Independent Flip {(iflip ? "yes" : "no")}");
            if (!supported)
                return;
            IPresentationManager* manager = null;
            Need(_factory->CreatePresentationManager(&manager), "CreatePresentationManager");
            _manager = manager;

            if (!Textures(display, record))
                return;

            // Every ask answered yes: the surface on a window through DirectComposition, and present statistics on.
            HANDLE handle;
            Need(DCompositionCreateSurfaceHandle(CompositionObjectAllAccess, null, &handle), "DCompositionCreateSurfaceHandle");
            _surfaceHandle = handle;
            IPresentationSurface* surface = null;
            Need(_manager->CreatePresentationSurface(_surfaceHandle, &surface), "CreatePresentationSurface");
            _surface = surface;
            _window = new BenchWindow("PlayMap bench: composition swapchain", display.X, display.Y, display.Width, display.Height, visible: true, borderless: true);
            IDCompositionDevice* dcomp = null;
            Need(DCompositionCreateDevice(null, __uuidof<IDCompositionDevice>(), (void**)&dcomp), "DCompositionCreateDevice");
            _dcomp = dcomp;
            IDCompositionTarget* target = null;
            Need(_dcomp->CreateTargetForHwnd(_window.Handle, true, &target), "CreateTargetForHwnd");
            _target = target;
            IDCompositionVisual* visual = null;
            Need(_dcomp->CreateVisual(&visual), "CreateVisual");
            _visual = visual;
            IUnknown* content = null;
            Need(_dcomp->CreateSurfaceFromHandle(_surfaceHandle, &content), "CreateSurfaceFromHandle");
            _content = content;
            Need(_visual->SetContent(_content), "SetContent");
            Need(_target->SetRoot(_visual), "SetRoot");
            Need(_dcomp->Commit(), "Commit");
            Need(_manager->EnablePresentStatisticsKind(PresentStatisticsKind_PresentStatus, 1), "EnablePresentStatisticsKind(PresentStatus)");
            Ready = true;
        }
        finally
        {
            if (adapter is not null)
                _ = adapter->Release();
            if (dxgi is not null)
                _ = dxgi->Release();
        }
    }

    /// <summary>Every ask answered yes, so the path presents.</summary>
    public bool Ready { get; }

    public string Name => "composition";

    public bool Closed => BenchWindow.Closed;

    /// <summary>Two shareable Direct3D 12 textures with a render-target view each, handed to the manager; false (the answer
    /// recorded) when it refuses one.</summary>
    private bool Textures(BenchDisplay display, PresentRecord record)
    {
        var heapDesc = new D3D12_DESCRIPTOR_HEAP_DESC { Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV, NumDescriptors = Buffers };
        ID3D12DescriptorHeap* heap = null;
        Need(_device->CreateDescriptorHeap(&heapDesc, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "CreateDescriptorHeap(RTV)");
        _rtvHeap = heap;
        _rtvStep = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        var properties = new D3D12_HEAP_PROPERTIES { Type = D3D12_HEAP_TYPE_DEFAULT };
        var desc = D3D12_RESOURCE_DESC.Tex2D(DXGI_FORMAT_B8G8R8A8_UNORM, (ulong)display.Width, (uint)display.Height, 1, 1, 1, 0,
            D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET, D3D12_TEXTURE_LAYOUT_UNKNOWN, 0);
        for (var i = 0; i < Buffers; i++)
        {
            ID3D12Resource* texture = null;
            Need(_device->CreateCommittedResource(&properties, D3D12_HEAP_FLAG_SHARED, &desc, D3D12_RESOURCE_STATE_COMMON, null,
                __uuidof<ID3D12Resource>(), (void**)&texture), "CreateCommittedResource(shared B8G8R8A8)");
            _textures[i] = (nint)texture;
            _device->CreateRenderTargetView(texture, null, Rtv(i));
            IPresentationBuffer* buffer = null;
            var hr = _manager->AddBufferFromResource((IUnknown*)texture, &buffer);
            record.Note(Words(string.Create(CultureInfo.InvariantCulture, $"composition: AddBufferFromResource(Direct3D 12 texture {i})"), hr));
            if (FAILED(hr))
                return false;
            _buffers[i] = (nint)buffer;
            HANDLE available;
            Need(buffer->GetAvailableEvent(&available), "IPresentationBuffer::GetAvailableEvent");
            _available[i] = available;
        }
        ID3D12CommandAllocator* allocator = null;
        Need(_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof<ID3D12CommandAllocator>(), (void**)&allocator), "CreateCommandAllocator");
        _allocator = allocator;
        ID3D12GraphicsCommandList* list = null;
        Need(_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, _allocator, null, __uuidof<ID3D12GraphicsCommandList>(), (void**)&list), "CreateCommandList");
        _list = list;
        Need(_list->Close(), "Close");
        ID3D12Fence* fence = null;
        Need(_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof<ID3D12Fence>(), (void**)&fence), "CreateFence");
        _fence = fence;
        return true;
    }

    private D3D12_CPU_DESCRIPTOR_HANDLE Rtv(int i)
    {
        var start = _rtvHeap->GetCPUDescriptorHandleForHeapStart();
        return new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = start.ptr + (nuint)(i * _rtvStep) };
    }

    public void Frame(long frame, bool white, PresentRecord record)
    {
        var i = (int)(frame % Buffers);
        // A buffer is drawn again only once the compositor has let it go (its available event), and the frame before it is done on
        // the GPU (each frame is waited for below, so one allocator serves every frame).
        _ = WaitForSingleObject(_available[i], 1000);
        Need(_allocator->Reset(), "ID3D12CommandAllocator::Reset");
        Need(_list->Reset(_allocator, null), "ID3D12GraphicsCommandList::Reset");
        var texture = (ID3D12Resource*)_textures[i];
        var toTarget = D3D12_RESOURCE_BARRIER.InitTransition(texture, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_RENDER_TARGET,
            AllSubresources, D3D12_RESOURCE_BARRIER_FLAG_NONE);
        _list->ResourceBarrier(1, &toTarget);
        var black = stackalloc float[] { 0, 0, 0, 1 };
        var lit = stackalloc float[] { 1, 1, 1, 1 };
        _list->ClearRenderTargetView(Rtv(i), black, 0, null);
        if (white)
        {
            var quad = _quad;
            _list->ClearRenderTargetView(Rtv(i), lit, 1, &quad);
        }
        var toCommon = D3D12_RESOURCE_BARRIER.InitTransition(texture, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_COMMON,
            AllSubresources, D3D12_RESOURCE_BARRIER_FLAG_NONE);
        _list->ResourceBarrier(1, &toCommon);
        Need(_list->Close(), "ID3D12GraphicsCommandList::Close");
        var lists = (ID3D12CommandList*)_list;
        _queue->ExecuteCommandLists(1, &lists);
        Need(_queue->Signal(_fence, ++_signalled), "ID3D12CommandQueue::Signal");
        WaitFor(_signalled);

        Need(_surface->SetBuffer((IPresentationBuffer*)_buffers[i]), "IPresentationSurface::SetBuffer");
        QueryInterruptTimePrecise(out var now);
        var target = now + 2 * _refresh100ns;
        var id = _manager->GetNextPresentId();
        Need(_manager->SetTargetTime(new SystemInterruptTime { value = target }), "SetTargetTime");
        var before = Stopwatch.GetTimestamp();
        var hr = _manager->Present();
        var after = Stopwatch.GetTimestamp();
        record.Row(Name, frame, white, before, after, id, 0, 0, 0,
            string.Create(CultureInfo.InvariantCulture, $"present 0x{hr.Value:X8} target {target} (interrupt time, 100 ns){Statistics()}"));
        BenchWindow.Pump();
    }

    /// <summary>Every present status the manager has reported since the last frame: "; id 41 queued".</summary>
    private string Statistics()
    {
        var words = "";
        for (var n = 0; n < 16; n++)
        {
            IPresentStatistics* statistics = null;
            if (FAILED(_manager->GetNextPresentStatistics(&statistics)) || statistics is null)
                break;
            IPresentStatusPresentStatistics* status = null;
            if (SUCCEEDED(statistics->QueryInterface(__uuidof<IPresentStatusPresentStatistics>(), (void**)&status)))
            {
                words += string.Create(CultureInfo.InvariantCulture, $"; id {status->GetPresentId()} {status->GetPresentStatus()}");
                _ = status->Release();
            }
            _ = statistics->Release();
        }
        return words;
    }

    private void WaitFor(ulong value)
    {
        if (value == 0 || _fence->GetCompletedValue() >= value)
            return;
        Need(_fence->SetEventOnCompletion(value, _event), "ID3D12Fence::SetEventOnCompletion");
        _ = WaitForSingleObject(_event, 5000);
    }

    private static string Words(string ask, HRESULT hr) => string.Create(CultureInfo.InvariantCulture,
        $"{ask} {(SUCCEEDED(hr) ? "took it" : "refused it")} (0x{hr.Value:X8})");

    private static void Need(HRESULT hr, string call)
    {
        if (FAILED(hr))
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{call} answered 0x{hr.Value:X8}"));
    }

    public void Dispose()
    {
        if (_queue is not null && _fence is not null && SUCCEEDED(_queue->Signal(_fence, ++_signalled)))
            WaitFor(_signalled);
        foreach (var b in _buffers.Where(b => b != 0))
            _ = ((IPresentationBuffer*)b)->Release();
        foreach (var t in _textures.Where(t => t != 0))
            _ = ((ID3D12Resource*)t)->Release();
        Release(ref _content);
        Release(ref _visual);
        Release(ref _target);
        Release(ref _dcomp);
        Release(ref _surface);
        Release(ref _manager);
        Release(ref _factory);
        Release(ref _fence);
        Release(ref _list);
        Release(ref _allocator);
        Release(ref _rtvHeap);
        Release(ref _queue);
        Release(ref _device);
        foreach (var a in _available.Where(a => a != HANDLE.NULL))
            _ = CloseHandle(a);
        if (_surfaceHandle != HANDLE.NULL)
            _ = CloseHandle(_surfaceHandle);
        if (_event != HANDLE.NULL)
            _ = CloseHandle(_event);
        _window?.Dispose();
    }

    private static void Release<T>(ref T* pointer) where T : unmanaged
    {
        if (pointer is not null)
            _ = ((IUnknown*)pointer)->Release();
        pointer = null;
    }
}
