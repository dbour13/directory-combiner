# directory-combiner

Creates a virtual drive that combines drives or directories together.  Mostly wanted this as I have media across several
drives that I would like to access from a single folder, but it also lets you map different drives to their own folder if
you want.

You will need to install Dokan from here first: https://github.com/dokan-dev/dokany/releases

I've mostly copied the code from the Mirror example here: https://github.com/dokan-dev/dokan-dotnet/tree/master/sample/DokanNetMirror

Use this at your own risk. I accept no responsibility for any data lost.

## Usage

```
Usage:
  directory-combiner [options]

Options:
  -md, --mount-drive-letter <md>    The drive letter to mount to.
  -mf, --mirror-folders <mf>        Folder to mirror in the virtual drive. Can specify multiple. Each should be in the
                                    format "\physical\path\to\directory|\virtual\directory"
  --version                         Display version information
```

## Example
An example of mirroring two folders "J:\Media\Videos" and "H:\Media\Videos" into a single folder, "videos", on virtual drive "K:\":

```
directory-combiner -md K:\ -mf "J:\Media\Videos|\videos" -mf "H:\Media\Videos|\videos"
```

An example of mirroring two folders "J:\Media\Videos" and "H:\Media\Videos" into separate folders, "j\videos" and "h\videos" on virtual drive "K:\":

```
directory-combiner -md K:\ -mf "J:\Media\Videos|\j\videos" -mf "H:\Media\Videos|\h\videos"
```

## Third Party Licenses
* DokanNet (MIT)
* System.CommandLine (MIT)