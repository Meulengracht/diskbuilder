# diskbuilder

A utility tool to build disk images for virtual machines. Configurable by YAML and easily extended. It uses the DiscUtils and is
written in .Net Core 3.1.

Supported disk image types
- img
- vmdk

Supported filesystems
- FAT (16 for sizes under 512mb, 32 for sizes above)
- MFS (Filesystem used by MollenOS)

The disk builder also supports writing stage1 and stage 2 bootloaders. How this is implemented or done is up to the individual filesystem implementation. For FAT and MFS the stage 2 bootloader is written to the reserved sectors that are available for those systems.

## Building

To build and run the project you must have install dotnetcore runtime and sdk.

For installation instructions of dotnet go to [Microsoft DOTNET Linux](https://docs.microsoft.com/en-us/dotnet/core/install/linux)
and make sure you install dotnet core 3.1.

The build system used is cmake, and no project options are present currently. To build
the project you can execute following commands

```
cd <project>
mkdir build
cd build
cmake -G "Unix Makefiles" ..
make
```

You should end up with the executable in build folder.

## Usage

You can now create disk images by executing the following command

```
./osbuilder --project <your_yaml> --target {img, vmdk}
```

When you builder is complete, you should end up with disk.{img,vmdk} in the folder
from where the builder is called.

## Configuration

Examples on yaml disk images can be found in the examples/ folder.

### GPT (UEFI) Disk Example

```
#####################
# scheme
#     values: {MBR, GPT}
#
# MBR
#     Formats the partition table in MBR format and installs a
#     predefined MBR bootsector that automatically loads the first VBR
#     that has a bootsignature.
# GPT
#     Formats the partition table in GPT format and installs a 
#     predefined hybrid MBR bootsector that automatically loads the first
#     partition VBR marked as legacy boot ('boot' attribute).
#
scheme: GPT
size: 2GB

# define partitions for disk
partitions:
  - label: efi-boot

    # FAT16 for any size less than 512mb, size must be above 64mb
    type: FAT

    # EFI System GUID
    guid: C12A7328-F81F-11D2-BA4B-00A0C93EC93B
    size: 128MB
    sources:
      - type: file
        path: <path_to_bootloader>/BOOT.efi
        target: /EFI/BOOT/BOOTX64.EFI
      - type: dir
        path: <path_to_additional_bootfiles>
        target: /EFI/vali/

  - label: os-boot
    type: MFS
    guid: C4483A10-E3A0-4D3F-B7CC-C04A6E16612B
    # attributes can be: boot, readonly, shadow, noautomount
    attributes:
      - boot
      - readonly
      - noautomount
    size: 128MB
    sources:
      - type: dir
        path: deploy/hdd/boot
        target: /

  - label: vali-data
    type: MFS
    guid: 80C6C62A-B0D6-4FF4-A69D-558AB6FD8B53
    sources:
      - type: dir
        path: deploy/hdd/shared
        target: /
```
