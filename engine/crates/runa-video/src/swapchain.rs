use windows::{
    core::*,
    Win32::Foundation::*,
    Win32::Graphics::Direct3D::*,
    Win32::Graphics::Direct3D11::*,
    Win32::Graphics::Dxgi::Common::*,
    Win32::Graphics::Dxgi::*,
};

/// ISwapChainPanelNative GUID in WinUI 3 (Windows App SDK / Microsoft.UI.Xaml)
pub const IID_ISWAPCHAINPANELNATIVE: GUID = GUID::from_u128(0x63aad0b8_7c24_40ff_85a8_640d944cc325);

#[repr(C)]
#[allow(non_snake_case)]
pub struct ISwapChainPanelNativeVtbl {
    pub base: IUnknown_Vtbl,
    pub SetSwapChain: unsafe extern "system" fn(this: *mut std::ffi::c_void, swap_chain: *mut std::ffi::c_void) -> HRESULT,
}

pub struct D3D11RenderEngine {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    dxgi_device: IDXGIDevice,
    factory: IDXGIFactory2,
    swap_chain: Option<IDXGISwapChain1>,
    width: u32,
    height: u32,
}

impl D3D11RenderEngine {
    pub fn new() -> Result<Self> {
        let mut device: Option<ID3D11Device> = None;
        let mut context: Option<ID3D11DeviceContext> = None;

        let flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        let feature_levels = [
            D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL_10_1,
        ];

        // SAFETY: Calling documented D3D11CreateDevice API with valid parameters
        unsafe {
            D3D11CreateDevice(
                None,
                D3D_DRIVER_TYPE_HARDWARE,
                HMODULE::default(),
                flags,
                Some(&feature_levels),
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                Some(&mut context),
            )?;
        }

        let device = device.ok_or_else(|| Error::from(E_FAIL))?;
        let context = context.ok_or_else(|| Error::from(E_FAIL))?;
        let dxgi_device: IDXGIDevice = device.cast()?;
        let adapter: IDXGIAdapter = unsafe { dxgi_device.GetAdapter()? };
        let factory: IDXGIFactory2 = unsafe { adapter.GetParent()? };

        Ok(Self {
            device,
            context,
            dxgi_device,
            factory,
            swap_chain: None,
            width: 0,
            height: 0,
        })
    }

    pub fn adapter_name(&self) -> String {
        // SAFETY: DXGI GetDesc call
        unsafe {
            if let Ok(adapter) = self.dxgi_device.GetAdapter() {
                if let Ok(desc) = adapter.GetDesc() {
                    let len = desc.Description.iter().position(|&c| c == 0).unwrap_or(desc.Description.len());
                    return String::from_utf16_lossy(&desc.Description[..len]);
                }
            }
        }
        "Unknown Adapter".to_string()
    }

    pub fn create_composition_swap_chain(&mut self, width: u32, height: u32) -> Result<IDXGISwapChain1> {
        let desc = DXGI_SWAP_CHAIN_DESC1 {
            Width: width,
            Height: height,
            Format: DXGI_FORMAT_B8G8R8A8_UNORM,
            Stereo: FALSE,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: 2,
            Scaling: DXGI_SCALING_STRETCH,
            SwapEffect: DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
            AlphaMode: DXGI_ALPHA_MODE_PREMULTIPLIED,
            Flags: 0,
        };

        // SAFETY: CreateSwapChainForComposition with valid factory, device and desc
        let swap_chain = unsafe {
            self.factory.CreateSwapChainForComposition(&self.device, &desc, None)?
        };

        self.width = width;
        self.height = height;
        self.swap_chain = Some(swap_chain.clone());
        Ok(swap_chain)
    }

    pub fn present_clear_frame(&self, r: f32, g: f32, b: f32, a: f32) -> Result<()> {
        let sc = self.swap_chain.as_ref().ok_or_else(|| Error::from(E_FAIL))?;
        // SAFETY: GetBuffer and render target creation
        unsafe {
            let back_buffer: ID3D11Texture2D = sc.GetBuffer(0)?;
            let mut rtv: Option<ID3D11RenderTargetView> = None;
            self.device.CreateRenderTargetView(&back_buffer, None, Some(&mut rtv))?;
            let rtv = rtv.ok_or_else(|| Error::from(E_FAIL))?;

            let color = [r, g, b, a];
            self.context.ClearRenderTargetView(&rtv, &color);
            sc.Present(0, DXGI_PRESENT(0)).ok()?;
        }
        Ok(())
    }

    /// Attaches the native swap chain to a WinUI 3 `ISwapChainPanelNative` interface.
    ///
    /// # Safety
    /// The caller must ensure that `panel_native` points to a valid `ISwapChainPanelNative` COM object.
    pub unsafe fn set_surface_panel(&self, panel_native: *mut std::ffi::c_void) -> Result<()> {
        let sc = self.swap_chain.as_ref().ok_or_else(|| Error::from(E_FAIL))?;
        if panel_native.is_null() {
            return Err(Error::from(E_POINTER));
        }

        // SAFETY: Calling COM ISwapChainPanelNative::SetSwapChain with verified raw pointer
        let vtbl = *(panel_native as *mut *mut ISwapChainPanelNativeVtbl);
        let sc_ptr = sc.as_raw();
        let hr = ((*vtbl).SetSwapChain)(panel_native, sc_ptr);
        if hr.is_err() {
            return Err(Error::from(hr));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_d3d11_device_and_composition_swapchain_creation() {
        let mut engine = D3D11RenderEngine::new().expect("D3D11 device creation failed");
        let adapter = engine.adapter_name();
        println!("GPU Adapter: {}", adapter);
        assert!(!adapter.is_empty());

        let _swapchain = engine.create_composition_swap_chain(1920, 1080)
            .expect("CreateSwapChainForComposition failed");
        assert!(engine.swap_chain.is_some());

        let res = engine.present_clear_frame(0.05, 0.05, 0.1, 1.0);
        assert!(res.is_ok(), "Present clear frame failed: {:?}", res);
    }
}
