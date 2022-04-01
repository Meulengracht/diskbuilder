# diskbuilder

A utility tool to build disk images for virtual machines. Configurable by YAML and easily extended. It uses the DiscUtils library and is
written in .Net Core 3.1.

Originally developed for the Vali/MollenOS operating system, this is a generic virtual machine image builder. It supports configuration in yaml, and supports installing sources into the VM image as a part of the build process. It also supports installing bootloaders (stage1 and stage2) onto the partitions.

[![Get it from the Snap Store](https://snapcraft.io/static/images/badges/en/snap-store-black.svg)](https://snapcraft.io/diskbuilder)

## Features

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
./osbuilder <your_yaml> --target {img, vmdk}
```

When you builder is complete, you should end up with disk.{img,vmdk} in the folder
from where the builder is called.

## Configuration

Examples on yaml disk images can be found in the examples/ folder.

### YAML Specification

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

# Partitions that should be installed onto the disk image.
# Each partition will be right after each other on the disk image
# and can support different yaml attributes.
partitions:
    #####################
    # label
    # The label for this partition. For some filesystems this label
    # will also be the name the partition will appear under. For FAT
    # this label must be no longer than 11 bytes.
    #
  - label: efi-boot
    
    #####################
    # type
    #     values: {FAT, MFS}
    #
    # FAT
    # FAT16 for any size less than 512mb, size must be above 64mb. FAT supports installation
    # of a stage1 and stage2 bootloader.
    #
    # MFS
    # MFS is natively supported by this tool, but is a custom, not very good filesystem
    # for MollenOS/Vali. Don't use this as it is subject to being changed. MFS supports
    # installation of stage1 and stage2 bootloaders.
    #
    type: FAT

    #####################
    # guid
    # The guid for the partition, this is only used if the disk schema is GPT or
    # if the filesystem has any guid identifier in it's superblock. The GUID present
    # below is the EFI System GUID.
    #
    guid: C12A7328-F81F-11D2-BA4B-00A0C93EC93B

    #####################
    # size
    # The size of this partition. This cannot be more than all the partitions combined
    # or be larger than the disk size. For the last partition its not required to specify
    # the partition size, as this will resize the partition to the last remaining disk capacity.
    # 
    size: 128MB
    
    #####################
    # attributes
    #     values: {boot, readonly, shadow, noautomount}
    #
    # Partition attributes, primarily used by the GPT table or if the filesystem
    # supports them. When using GPT and want to mark a partition as BIOS bootable
    # the 'boot' attribute must be specified for this partition.
    attributes:
      - boot
      - readonly
      - noautomount

    #####################
    # vbr-image
    vbr-image: stage1.bin

    #####################
    # reserved-sectors-image
    reserved-sectors-image: stage2.bin
    
    #####################
    # sources
    # Sources are the files/directories that should be installed onto
    # the disk partition as a part of the build process.
    #
    sources:

        #####################
        # source.type
        #     values: {file, dir, chef}
        # 
        # file
        # This installs a single file from host location 'path' to partition location 'target'.
        #
        # dir
        # This copies the entire contents (recursively) into the partition, like the file parameter
        # the 'path' key specifies where on the host machine the directory is, and the 'target' key
        # specifies the directory the contents should be copied into on the disk partition.
        #
        # chef
        # Osbuilder supports chef packages, and can download and install those directly to the partition
        # if specified. The 'target' key specifies where on the partition the package should be copied to,
        # this can be either a full file path (must contain an extension) or a directory path.
      - type: file

        #####################
        # source.path
        # If the path is a relative path, it will be resolved from where osbuilder is invoked.
        #
        path: <path_to_bootloader>/BOOT.efi

        #####################
        # source.target
        # Must be an absolute path from the root of the partition.
        target: /EFI/BOOT/BOOTX64.EFI
        
      - type: dir
        path: <path_to_additional_bootfiles>
        target: /EFI/vali/
        
        #####################
        # source.type.chef
        #
        # The chef plugin for sources supports some additional keys to specify
        # the package and its configuration.
      - type: chef
        package: vali/hello-world
        channel: stable
        platform: my-os
        arch: x64
        target: packages/
```
