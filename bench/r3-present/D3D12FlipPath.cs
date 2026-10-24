using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D_FEATURE_LEVEL;
using static TerraFX.Interop.DirectX.D3D12_COMMAND_LIST_TYPE;
using static TerraFX.Interop.DirectX.D3D12_DESCRIPTOR_HEAP_TYPE;
using static TerraFX.Interop.DirectX.D3D12_FENCE_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_BARRIER_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_STATES;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.DirectX.DXGI;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.DirectX.DXGI_SWAP_CHAIN_FLAG;
using static TerraFX.Interop.DirectX.DXGI_SWAP_EFFECT;
using static TerraFX.Interop.Windows.Windows;

namespace PlayMap.Bench.Present;

/// <summary>The raw Direct3D 12 flip path, nothing of PlayMap's renderer in it: a device on the adapter that drives the display
/// (or WARP), a borderless window over the whole display, a FLIP_DISCARD swap chain of three buffers with a frame-latency waitable
/// at one frame, and each frame cleared black with the quad cleared white on flash frames — no shader, so nothing but the
/// presentation is measured. Each present's QPC instants, DXGI's present count and frame statistics go to the record.</summary>
[SupportedOSPlatform("windows10.0.19043")]
internal sealed unsafe class D3D12FlipPath : IDisposable
{
    private const int Buffers = 3;
    private const uint AllSubresources = 0xFFFFFFFF; // D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES

    private readonly BenchWindow _window;
    private readonly RECT _quad;
    private readonly nint[] _targets = new nint[Buffers];
    private readonly nint[] _allocators = new nint[Buffers];
    private readonly ulong[] _done = new ulong[Buffers];
    private ID3D12Device* _device;
    private ID3D12CommandQueue* _queue;
    private IDXGISwapChain3* _chain;
    private ID3D12DescriptorHeap* _rtvHeap;
    private ID3D12GraphicsCommandList* _list;
    private ID3D12Fence* _fence;
    private readonly uint _rtvStep;
    private readonly HANDLE _event;
    private readonly HANDLE _waitable;
    private ulong _signalled;

    public D3D12FlipPath(BenchDisplay display, bool warp, PresentRecord record)
    {
        _quad = display.Quad;
        IDXGIFactory6* factory = null;
        IDXGIAdapter1* adapter = null;
        IDXGISwapChain1* chain1 = null;
        IDXGISwapChain2* chain2 = null;
        try
        {
            Need(CreateDXGIFactory2(0, __uuidof<IDXGIFactory6>(), (void**)&factory), "CreateDXGIFactory2");
            if (warp)
                Need(factory->EnumWarpAdapter(__uuidof<IDXGIAdapter1>(), (void**)&adapter), "EnumWarpAdapter");
            else
                Need(factory->EnumAdapters1(display.Adapter, &adapter), "EnumAdapters1");
            ID3D12Device* device = null;
            Need(D3D12CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device), "D3D12CreateDevice");
            _device = device;

            var queueDesc = new D3D12_COMMAND_QUEUE_DESC { Type = D3D12_COMMAND_LIST_TYPE_DIRECT };
            ID3D12CommandQueue* queue = null;
            Need(_device->CreateCommandQueue(&queueDesc, __uuidof<ID3D12CommandQueue>(), (void**)&queue), "CreateCommandQueue");
            _queue = queue;

            _window = new BenchWindow("PlayMap bench: Direct3D 12 flip", display.X, display.Y, display.Width, display.Height, visible: true, borderless: true);
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)display.Width,
                Height = (uint)display.Height,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = Buffers,
                SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD,
                Flags = (uint)DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
            };
            Need(factory->CreateSwapChainForHwnd((IUnknown*)_queue, _window.Handle, &desc, null, null, &chain1), "CreateSwapChainForHwnd");
            IDXGISwapChain3* chain = null;
            Need(chain1->QueryInterface(__uuidof<IDXGISwapChain3>(), (void**)&chain), "QueryInterface(IDXGISwapChain3)");
            _chain = chain;
            Need(chain1->QueryInterface(__uuidof<IDXGISwapChain2>(), (void**)&chain2), "QueryInterface(IDXGISwapChain2)");
            Need(chain2->SetMaximumFrameLatency(1), "SetMaximumFrameLatency(1)");
            _waitable = chain2->GetFrameLatencyWaitableObject();

            var heapDesc = new D3D12_DESCRIPTOR_HEAP_DESC { Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV, NumDescriptors = Buffers };
            ID3D12DescriptorHeap* heap = null;
            Need(_device->CreateDescriptorHeap(&heapDesc, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "CreateDescriptorHeap(RTV)");
            _rtvHeap = heap;
            _rtvStep = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
            for (var i = 0; i < Buffers; i++)
            {
                ID3D12Resource* target = null;
                Need(_chain->GetBuffer((uint)i, __uuidof<ID3D12Resource>(), (void**)&target), "GetBuffer");
                _targets[i] = (nint)target;
                _device->CreateRenderTargetView(target, null, Rtv(i));
                ID3D12CommandAllocator* allocator = null;
                Need(_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof<ID3D12CommandAllocator>(), (void**)&allocator), "CreateCommandAllocator");
                _allocators[i] = (nint)allocator;
            }
            ID3D12GraphicsCommandList* list = null;
            Need(_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, (ID3D12CommandAllocator*)_allocators[0], null,
                __uuidof<ID3D12GraphicsCommandList>(), (void**)&list), "CreateCommandList");
            _list = list;
            Need(_list->Close(), "Close");
            ID3D12Fence* fence = null;
            Need(_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof<ID3D12Fence>(), (void**)&fence), "CreateFence");
            _fence = fence;
            _event = CreateEventW(null, false, false, null);
            DXGI_ADAPTER_DESC1 adapterDesc;
            _ = adapter->GetDesc1(&adapterDesc);
            record.Note(string.Create(CultureInfo.InvariantCulture,
                $"d3d12: {new string((char*)&adapterDesc.Description).TrimEnd('\0')}, {display.Width}x{display.Height} on {display.Name}, flip discard, 3 buffers, waitable at 1 frame"));
        }
        finally
        {
            if (chain2 is not null)
                _ = chain2->Release();
            if (chain1 is not null)
                _ = chain1->Release();
            if (adapter is not null)
                _ = adapter->Release();
            if (factory is not null)
                _ = factory->Release();
        }
    }

    public string Name => "d3d12";

    public bool Closed => BenchWindow.Closed;

    private D3D12_CPU_DESCRIPTOR_HANDLE Rtv(int i)
    {
        var start = _rtvHeap->GetCPUDescriptorHandleForHeapStart();
        return new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = start.ptr + (nuint)(i * _rtvStep) };
    }

    public void Frame(long frame, bool white, PresentRecord record)
    {
        _ = WaitForSingleObjectEx(_waitable, 1000, true);
        var i = (int)_chain->GetCurrentBackBufferIndex();
        WaitFor(_done[i]);
        var allocator = (ID3D12CommandAllocator*)_allocators[i];
        Need(allocator->Reset(), "ID3D12CommandAllocator::Reset");
        Need(_list->Reset(allocator, null), "ID3D12GraphicsCommandList::Reset");
        var target = (ID3D12Resource*)_targets[i];
        var toTarget = D3D12_RESOURCE_BARRIER.InitTransition(target, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET,
            AllSubresources, D3D12_RESOURCE_BARRIER_FLAG_NONE);
        _list->ResourceBarrier(1, &toTarget);
        var black = stackalloc float[] { 0, 0, 0, 1 };
        var lit = stackalloc float[] { 1, 1, 1, 1 };
        var rtv = Rtv(i);
        _list->ClearRenderTargetView(rtv, black, 0, null);
        if (white)
        {
            var quad = _quad;
            _list->ClearRenderTargetView(rtv, lit, 1, &quad);
        }
        var toPresent = D3D12_RESOURCE_BARRIER.InitTransition(target, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT,
            AllSubresources, D3D12_RESOURCE_BARRIER_FLAG_NONE);
        _list->ResourceBarrier(1, &toPresent);
        Need(_list->Close(), "ID3D12GraphicsCommandList::Close");
        var lists = (ID3D12CommandList*)_list;
        _queue->ExecuteCommandLists(1, &lists);

        var before = Stopwatch.GetTimestamp();
        var hr = _chain->Present(1, 0);
        var after = Stopwatch.GetTimestamp();
        Need(_queue->Signal(_fence, ++_signalled), "ID3D12CommandQueue::Signal");
        _done[i] = _signalled;

        uint count = 0;
        _ = _chain->GetLastPresentCount(&count);
        DXGI_FRAME_STATISTICS stats;
        var statsHr = _chain->GetFrameStatistics(&stats);
        var read = SUCCEEDED(statsHr);
        record.Row(Name, frame, white, before, after, count, read ? stats.PresentRefreshCount : 0, read ? stats.SyncRefreshCount : 0,
            read ? stats.SyncQPCTime.QuadPart : 0,
            string.Create(CultureInfo.InvariantCulture, $"present 0x{hr.Value:X8} statistics 0x{statsHr.Value:X8} for present {(read ? stats.PresentCount : 0)}"));
        BenchWindow.Pump();
    }

    private void WaitFor(ulong value)
    {
        if (value == 0 || _fence->GetCompletedValue() >= value)
            return;
        Need(_fence->SetEventOnCompletion(value, _event), "ID3D12Fence::SetEventOnCompletion");
        _ = WaitForSingleObject(_event, 5000);
    }

    private static void Need(HRESULT hr, string call)
    {
        if (FAILED(hr))
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{call} answered 0x{hr.Value:X8}"));
    }

    public void Dispose()
    {
        if (_queue is not null && _fence is not null)
        {
            if (SUCCEEDED(_queue->Signal(_fence, ++_signalled)))
                WaitFor(_signalled);
        }
        if (_fence is not null)
            _ = _fence->Release();
        if (_list is not null)
            _ = _list->Release();
        foreach (var a in _allocators.Where(a => a != 0))
            _ = ((ID3D12CommandAllocator*)a)->Release();
        foreach (var t in _targets.Where(t => t != 0))
            _ = ((ID3D12Resource*)t)->Release();
        if (_rtvHeap is not null)
            _ = _rtvHeap->Release();
        if (_chain is not null)
            _ = _chain->Release();
        if (_queue is not null)
            _ = _queue->Release();
        if (_device is not null)
            _ = _device->Release();
        if (_event != HANDLE.NULL)
            _ = CloseHandle(_event);
        _window.Dispose();
        _fence = null;
        _list = null;
        _rtvHeap = null;
        _chain = null;
        _queue = null;
        _device = null;
    }
}
