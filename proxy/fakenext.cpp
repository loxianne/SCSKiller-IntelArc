// Test stand-in for a mod's d3d12.dll that the proxy chains to (scskiller.ini next=): forwards D3D12CreateDevice to the
// system dll and counts the calls. Only D3D12CreateDevice, like many wrappers: the proxy takes the rest from the system dll.
// After FakeNext_SwapPs it acts like a shader-replacing wrapper (ReShade with a RenoDX addon): the game gets a wrapper
// device whose CreateGraphicsPipelineState passes the desc on with that pixel shader instead of the game's.
// After FakeNext_Layer it is a layer like ReShade as dxgi.dll instead: see there.
// Never shipped (build/publish.ps1 copies named files only); `selftest chain` loads it.
#include <windows.h>
#define D3D12CreateDevice D3D12CreateDevice_h  // d3d12.h declares it without dllexport
#include <d3d12.h>
#undef D3D12CreateDevice
#include <string>
#include <vector>

static LONG g_calls;
static std::string g_ps;
static bool g_unwrap, g_rewrite_rs;
static const GUID kUnwrapped = {0x7f2c9a11, 0x3b4e, 0x4d6a, {0x81, 0x2f, 0x5e, 0x9c, 0xd3, 0x7a, 0x1b, 0x42}};  // ReShade 6.8+

extern "C" __declspec(dllexport) LONG WINAPI FakeNext_Calls() { return g_calls; }
extern "C" __declspec(dllexport) void WINAPI FakeNext_SwapPs(const void* p, SIZE_T n) { g_ps.assign((const char*)p, n); }
// Copied as a game's D3D12\D3D12Core.dll: an Agility runtime newer than any system's that makes no device (WarmerTests)
extern "C" __declspec(dllexport) const UINT D3D12SDKVersion = 100000;

// The wrapper: every slot forwards to the same slot of the wrapped device (a thunk swaps `this`), but QueryInterface and
// CreateGraphicsPipelineState. 128 slots cover ID3D12Device14.
struct Wrap {
    void** vt;
    ID3D12Device* real;
};

static HRESULT STDMETHODCALLTYPE wrap_qi(Wrap* w, REFIID riid, void** pp) {
    if (g_unwrap && riid == kUnwrapped) return w->real->AddRef(), *pp = w->real, S_OK;
    HRESULT hr = w->real->QueryInterface(riid, pp);
    if (SUCCEEDED(hr) && *pp == w->real) *pp = w;  // every device interface stays the wrapper, as ReShade's do
    return hr;
}

static HRESULT STDMETHODCALLTYPE wrap_gfx(Wrap* w, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    D3D12_GRAPHICS_PIPELINE_STATE_DESC c = *d;
    if (c.PS.pShaderBytecode) c.PS = {g_ps.data(), g_ps.size()};
    return w->real->CreateGraphicsPipelineState(&c, riid, pp);
}

static HMODULE system_d3d12() {
    wchar_t sys[MAX_PATH];
    GetSystemDirectoryW(sys, MAX_PATH);
    return LoadLibraryW((std::wstring(sys) + L"\\d3d12.dll").c_str());
}

// RenoDX's injection: a root constant (b13, space 50) appended to every root signature, serialized anew.
static HRESULT STDMETHODCALLTYPE wrap_rs(Wrap* w, UINT mask, const void* blob, SIZE_T n, REFIID riid, void** pp) {
    if (!g_rewrite_rs) return w->real->CreateRootSignature(mask, blob, n, riid, pp);
    static HMODULE sys = system_d3d12();
    auto deser = (PFN_D3D12_CREATE_ROOT_SIGNATURE_DESERIALIZER)GetProcAddress(sys, "D3D12CreateRootSignatureDeserializer");
    auto ser = (PFN_D3D12_SERIALIZE_ROOT_SIGNATURE)GetProcAddress(sys, "D3D12SerializeRootSignature");
    ID3D12RootSignatureDeserializer* ds = nullptr;
    if (!deser || !ser || FAILED(deser(blob, n, IID_PPV_ARGS(&ds)))) return E_FAIL;
    auto d = *ds->GetRootSignatureDesc();
    std::vector<D3D12_ROOT_PARAMETER> p(d.pParameters, d.pParameters + d.NumParameters);
    D3D12_ROOT_PARAMETER c = {D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS};
    c.Constants = {13, 50, 4};
    p.push_back(c);
    d.NumParameters = (UINT)p.size(), d.pParameters = p.data();
    ID3DBlob *b = nullptr, *err = nullptr;
    HRESULT hr = ser(&d, D3D_ROOT_SIGNATURE_VERSION_1, &b, &err);
    if (SUCCEEDED(hr)) hr = w->real->CreateRootSignature(mask, b->GetBufferPointer(), b->GetBufferSize(), riid, pp);
    if (b) b->Release();
    if (err) err->Release();
    ds->Release();
    return hr;
}

static void** wrapper_vtable() {
    static void** vt = [] {
        const int n = 128;
        auto code = (uint8_t*)VirtualAlloc(nullptr, n * 16, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        auto t = new void*[n];
        for (int i = 0; i < n; ++i) {
            uint8_t* c = code + i * 16;
            const uint8_t op[] = {0x48, 0x8B, 0x49, 0x08, 0x48, 0x8B, 0x01, 0xFF, 0xA0};  // mov rcx,[rcx+8]; mov rax,[rcx]; jmp [rax+disp32]
            int32_t disp = i * 8;
            memcpy(c, op, sizeof op), memcpy(c + sizeof op, &disp, 4);
            t[i] = c;
        }
        FlushInstructionCache(GetCurrentProcess(), code, n * 16);
        t[0] = (void*)wrap_qi, t[10] = (void*)wrap_gfx, t[16] = (void*)wrap_rs;
        return t;
    }();
    return vt;
}

extern "C" __declspec(dllexport) HRESULT WINAPI D3D12CreateDevice(IUnknown* adapter, D3D_FEATURE_LEVEL fl, REFIID riid, void** pp) {
    static auto real = (decltype(&D3D12CreateDevice_h))GetProcAddress(system_d3d12(), "D3D12CreateDevice");
    InterlockedIncrement(&g_calls);
    if (!real) return E_FAIL;
    if (g_ps.empty() || !pp) return real(adapter, fl, riid, pp);
    ID3D12Device* dev = nullptr;
    HRESULT hr = real(adapter, fl, IID_PPV_ARGS(&dev));
    if (FAILED(hr)) return hr;
    auto w = new Wrap{wrapper_vtable(), dev};  // leaked: a test process
    hr = wrap_qi(w, riid, pp);
    dev->Release();
    return hr;
}

// FakeNext_Layer: a layer like ReShade as dxgi.dll with a RenoDX addon. Loaded before the proxy, it points the system
// d3d12.dll's D3D12CreateDevice export here (its export table entry: ReShade patches the code instead, and the proxy reaches
// either the same way), and the device comes wrapped with root signatures rewritten (wrap_rs). unwrap: the wrapper answers
// IID_UnwrappedObject with the real device, as ReShade 6.8 does; without it the proxy finds the real device another way.
static decltype(&D3D12CreateDevice_h) g_sys_create;

static HRESULT WINAPI layer_create(IUnknown* adapter, D3D_FEATURE_LEVEL fl, REFIID riid, void** pp) {
    if (!pp) return g_sys_create(adapter, fl, riid, pp);
    ID3D12Device* dev = nullptr;
    HRESULT hr = g_sys_create(adapter, fl, IID_PPV_ARGS(&dev));
    if (FAILED(hr)) return hr;
    auto w = new Wrap{wrapper_vtable(), dev};  // leaked: a test process
    hr = wrap_qi(w, riid, pp);
    dev->Release();
    return hr;
}

extern "C" __declspec(dllexport) BOOL WINAPI FakeNext_Layer(BOOL unwrap) {
    g_unwrap = unwrap, g_rewrite_rs = true;
    auto base = (uint8_t*)system_d3d12();
    if (!base) return FALSE;
    auto nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    auto ex = (IMAGE_EXPORT_DIRECTORY*)(base + nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress);
    auto names = (DWORD*)(base + ex->AddressOfNames);
    auto ords = (WORD*)(base + ex->AddressOfNameOrdinals);
    auto fns = (DWORD*)(base + ex->AddressOfFunctions);
    for (DWORD i = 0; i < ex->NumberOfNames; ++i) {
        if (strcmp((const char*)base + names[i], "D3D12CreateDevice")) continue;
        DWORD* slot = &fns[ords[i]];
        g_sys_create = (decltype(g_sys_create))(base + *slot);
        // the table holds 32-bit offsets from the image base: a jump stub past the image, within reach
        uint8_t* stub = nullptr;
        for (uintptr_t a = ((uintptr_t)base + nt->OptionalHeader.SizeOfImage + 0xFFFF) & ~(uintptr_t)0xFFFF; !stub && a < (uintptr_t)base + 0x7FFF0000; a += 0x10000)
            stub = (uint8_t*)VirtualAlloc((void*)a, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (!stub) return FALSE;
        void* to = (void*)layer_create;
        const uint8_t jmp[] = {0xFF, 0x25, 0, 0, 0, 0};  // jmp [rip]
        memcpy(stub, jmp, sizeof jmp), memcpy(stub + sizeof jmp, &to, 8);
        DWORD old;
        VirtualProtect(slot, 4, PAGE_READWRITE, &old);
        *slot = (DWORD)(stub - base);
        VirtualProtect(slot, 4, old, &old);
        return TRUE;
    }
    return FALSE;
}
